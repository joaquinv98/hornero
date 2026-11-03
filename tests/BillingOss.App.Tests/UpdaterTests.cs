using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BillingOss.App.Platform;
using BillingOss.Updater;
using BillingOss.Updates;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingOss.App.Tests;

/// <summary>
/// El actualizador (D-20, A7) contra la base local y un Docker falso: canal verificado, pedido de punta a punta nodo por
/// nodo, chequeos que frenan, reintento por lock_timeout, vuelta atrás cuando falla un nodo, nodo atrasado y
/// autoactualización. Nada sale a la red: el canal, la salud y el binario los sirve un manejador HTTP de la prueba.
/// </summary>
[TestClass]
public sealed class UpdaterTests
{
    private static readonly string Previous = Digest("billing-oss", 100);

    [TestMethod]
    public async Task ElCanalVerificadoQuedaEnLaBase_YUnoAlteradoNoPisaAlBueno()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        var node = lab.Node("n1");
        var channel = Channel(Release("0.1.1"));

        Assert.IsNull(await node.Engine.Check(channel, Sign(key, channel), CancellationToken.None));
        var alterado = channel.ToArray();
        alterado[^5] ^= 1;
        StringAssert.Contains(await node.Engine.Check(alterado, Sign(key, channel), CancellationToken.None), "no verifica");

        var state = await node.Engine.State(CancellationToken.None);
        Assert.AreEqual(1, UpdaterEngine.Channel(state)!.Releases.Count);
        Assert.IsTrue(await core.Query(db => db.Set<UpdaterState>().Select(s => s.CheckRejected).SingleAsync()));
        await node.Engine.Tick(CancellationToken.None);
        var overview = await core.Use<UpdateService, UpdateOverview>(s => s.Overview(DateTimeOffset.UtcNow));
        Assert.AreEqual(UpdateState.NoData, overview.Summary.State);
        Assert.AreEqual("Rejected", overview.Summary.Reason);
    }

    [TestMethod]
    public async Task UnPedidoDelPanel_SeAplicaNodoPorNodo_ConMarcaYMigracionesUnaSolaVez()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        var n1 = lab.Node("n1");
        var n2 = lab.Node("n2");
        await lab.Load(Channel(Release("0.1.1", from: "0.1.0")));
        await lab.Round();

        Actor.Current = new Actor(ActorKind.Staff, "staff:1", IPAddress.Loopback);
        var id = await core.Use<UpdateService, long>(s => s.Request("0.1.1"));
        var request = await lab.RunUntilFinished(id);

        Assert.AreEqual("done", request.Status, request.Error);
        Assert.AreEqual("0.1.1", await core.Query(db => db.Set<UpdaterState>().Select(s => s.Version).SingleAsync()));
        Assert.IsNull(await core.Query(db => db.Set<UpdaterState>().Select(s => s.SchedulerPausedUntil).SingleAsync()));
        CollectionAssert.AreEqual(
            new[] { "Started", "Pulled", "Pulled", "Mark", "Migrated", "NodeApplied", "NodeApplied", "Done" },
            UpdateService.Events(request).Select(e => e.Event).ToArray());
        CollectionAssert.AreEqual(new[] { "n1", "n2" }, UpdateService.Events(request).Where(e => e.Event == "NodeApplied").Select(e => e.Node).ToArray());

        // Migraciones una sola vez, con la imagen nueva y sin tocar compose.version.yaml antes de su turno.
        var migrations = lab.Commands.Where(c => c.Args.Contains("migrate")).ToList();
        Assert.HasCount(1, migrations);
        StringAssert.Contains(migrations[0].Env!["COMPOSE_FILE"], "compose.migrate.yaml");
        foreach (var node in new[] { n1, n2 })
        {
            var (version, images) = node.Host.Current();
            Assert.AreEqual(new Version(0, 1, 1), version);
            Assert.AreEqual(Digest("billing-oss", 11), images!["web"]);
            Assert.AreEqual(Digest("billing-oss-arca", 11), images["arca"]);
            Assert.IsFalse(images.ContainsKey("postgres"), "En HA PostgreSQL no va en el conjunto.");
            StringAssert.Contains(File.ReadAllText(Path.Combine(node.Dir, ".env")), "COMPOSE_FILE=compose.yaml:compose.version.yaml");
        }

        Assert.HasCount(1, Directory.GetFiles(lab.Marks, "*.dump"));
        var audited = await core.Query(db => db.Database.SqlQueryRaw<string>("SELECT action AS \"Value\" FROM platform.audit_events WHERE action = 'updates.requested'").ToListAsync());
        Assert.HasCount(1, audited);
    }

    [TestMethod]
    public async Task ConUnaCorridaDelCiclo_LosChequeosFrenan_SinTocarNingunNodo()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        lab.Node("n1");
        await lab.Load(Channel(Release("0.1.1")));
        await lab.Round();
        await core.Db(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Jobs.Add(new Job(JobKinds.BillingRun, "{}", now, now) { Status = JobStatus.Running, LockedUntil = now.AddMinutes(5) });
            return db.SaveChangesAsync();
        });

        var id = await lab.Console("0.1.1");
        var request = await lab.RunUntilFinished(id);

        Assert.AreEqual("rejected", request.Status);
        StringAssert.Contains(request.Error, "ciclo de facturación");
        Assert.IsFalse(lab.Commands.Any(c => c.Args.Contains("pull") || c.Args.Contains("up")));
        Assert.IsNull(await core.Query(db => db.Set<UpdaterState>().Select(s => s.SchedulerPausedUntil).SingleAsync()));
    }

    [TestMethod]
    public async Task ConMarcaPitr_SinArchivadoDeWal_FrenaEnLosChequeos()
    {
        // La base local de pruebas no archiva WAL: una marca con pg_create_restore_point no serviría para volver.
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key, recoveryMark: "restore-point");
        lab.Node("n1");
        await lab.Load(Channel(Release("0.1.1")));
        await lab.Round();

        var request = await lab.RunUntilFinished(await lab.Console("0.1.1"));

        Assert.AreEqual("rejected", request.Status);
        StringAssert.Contains(request.Error, "archivado de WAL");
    }

    [TestMethod]
    public async Task LasMigracionesReintentanSoloPorLockTimeout()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        var n1 = lab.Node("n1");
        await lab.Load(Channel(Release("0.1.1"), Release("0.1.2")));
        await lab.Round();

        lab.MigrateFailures.Enqueue("ERROR: 55P03: canceling statement due to lock timeout");
        var ok = await lab.RunUntilFinished(await lab.Console("0.1.1"));
        Assert.AreEqual("done", ok.Status, ok.Error);
        Assert.AreEqual(2, lab.Commands.Count(c => c.Args.Contains("migrate")));

        lab.MigrateFailures.Enqueue("ERROR: 42703: column \"x\" does not exist");
        var failed = await lab.RunUntilFinished(await lab.Console("0.1.2"));
        Assert.AreEqual("failed", failed.Status);
        StringAssert.Contains(failed.Error, "42703");
        Assert.AreEqual(new Version(0, 1, 1), n1.Host.Current().Version, "Una migración que falla no toca los nodos.");
    }

    [TestMethod]
    public async Task SiUnNodoFalla_VuelvenLosQueYaCambiaron_YLaVersionQuedaBloqueada()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        var n1 = lab.Node("n1");
        var n2 = lab.Node("n2");
        lab.Unhealthy["n2"] = new Version(0, 1, 1);
        await lab.Load(Channel(Release("0.1.1")));
        await lab.Round();

        var request = await lab.RunUntilFinished(await lab.Console("0.1.1"));

        Assert.AreEqual("failed", request.Status);
        StringAssert.Contains(request.Error, "n2");
        var events = UpdateService.Events(request).Select(e => e.Event).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Mark", "NodeApplied", "NodeFailed", "RollingBack", "RolledBack", "Failed" }, events);
        StringAssert.StartsWith(UpdateService.Events(request).Single(e => e.Event == "Mark").Detail, "billing-oss-0.1.0-a-0.1.1-pedido-");
        foreach (var node in new[] { n1, n2 })
        {
            Assert.AreEqual(new Version(0, 1, 0), node.Host.Current().Version, node.Name);
            Assert.IsNull(node.Host.Current().Images, "Vuelve a las etiquetas del .env, como arrancó.");
        }

        CollectionAssert.AreEqual(new[] { "0.1.1" }, await core.Query(db => db.Set<UpdaterState>().Select(s => s.BlockedVersions).SingleAsync()));
        var again = await Assert.ThrowsExactlyAsync<UpdateException>(() => core.Use<UpdateService, long>(s => s.Request("0.1.1")));
        StringAssert.Contains(again.Message, "bloqueada");
    }

    [TestMethod]
    public async Task UnNodoQueEstuvoApagado_SePoneAlDiaConLosDigestsDeLaInstalacion()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        lab.Node("n1");
        await lab.Load(Channel(Release("0.1.1")));
        await lab.Round();
        Assert.AreEqual("done", (await lab.RunUntilFinished(await lab.Console("0.1.1"))).Status);

        var tarde = lab.Node("n3");
        await tarde.Engine.Tick(CancellationToken.None);

        Assert.AreEqual(new Version(0, 1, 1), tarde.Host.Current().Version);
        Assert.AreEqual(Digest("billing-oss", 11), tarde.Host.Current().Images!["worker"]);
    }

    [TestMethod]
    public async Task UnNodoQueNoPudoVolverAntesDelPlazo_QuedaConFalla_YSePoneAlDiaCuandoVuelve()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        var n1 = lab.Node("n1");
        var n2 = lab.Node("n2");
        lab.Unhealthy["n2"] = new Version(0, 1, 2);
        await lab.Load(Channel(Release("0.1.1"), Release("0.1.2"), Release("0.1.3")));
        await lab.Round();
        Assert.AreEqual("done", (await lab.RunUntilFinished(await lab.Console("0.1.1"))).Status);

        // n1 aplica 0.1.2 y se apaga. n2 falla la salud, el pedido pasa a rollback y n1 no está para volver.
        var id = await lab.Console("0.1.2");
        await lab.RunUntil(id, r => UpdateService.Events(r).Any(e => e.Event == "NodeApplied" && e.Node == "n1"));
        lab.Off.Add("n1");
        await UpdatesTests.Owner(core, "UPDATE updates.state SET lease_until = now() - interval '1 second'");
        await lab.RunUntil(id, r => UpdateService.Events(r).Any(e => e.Event == "RollingBack"));
        await UpdatesTests.Owner(core, "UPDATE updates.requests SET deadline = now() - interval '1 second'");
        var request = await lab.RunUntilFinished(id);

        Assert.AreEqual("failed", request.Status);
        Assert.AreEqual(new Version(0, 1, 2), n1.Host.Current().Version, "Nadie pudo tocar a n1.");
        var stuck = (await n1.Engine.Nodes(CancellationToken.None))["n1"];
        Assert.AreEqual("failed", stuck.Status);
        StringAssert.Contains(stuck.Detail, "0.1.2");
        CollectionAssert.AreEqual(new[] { "0.1.2" }, await core.Query(db => db.Set<UpdaterState>().Select(s => s.BlockedVersions).SingleAsync()));

        // Cuando vuelve, converge a la versión de la instalación aunque sea más vieja, y la instalación puede seguir.
        lab.Off.Remove("n1");
        await n1.Engine.Tick(CancellationToken.None);
        Assert.AreEqual(new Version(0, 1, 1), n1.Host.Current().Version);
        Assert.AreEqual(Digest("billing-oss", 11), n1.Host.Current().Images!["web"]);
        Assert.AreEqual("ok", (await n1.Engine.Nodes(CancellationToken.None))["n1"].Status);
        var next = await lab.RunUntilFinished(await lab.Console("0.1.3"));
        Assert.AreEqual("done", next.Status, next.Error);
    }

    [TestMethod]
    public async Task DesdeElPanelNoSeVuelveDebajoDelParcheDeSeguridad_YDesdeLaConsolaSi_SinMigraciones()
    {
        await using var core = await CoreHarness.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var lab = new Lab(core, key);
        var n1 = lab.Node("n1");
        await lab.Load(Channel(Release("0.1.1"), Release("0.1.2", security: true)));
        await lab.Round();
        Assert.AreEqual("done", (await lab.RunUntilFinished(await lab.Console("0.1.2"))).Status);

        var web = await Assert.ThrowsExactlyAsync<UpdateException>(() => n1.Engine.Preview("0.1.1", "web", CancellationToken.None));
        StringAssert.Contains(web.Message, "por consola");
        var plan = await n1.Engine.Preview("0.1.1", "console", CancellationToken.None);
        Assert.IsTrue(plan.Back);

        var migrationsBefore = lab.Commands.Count(c => c.Args.Contains("migrate"));
        var back = await lab.RunUntilFinished(await lab.Console("0.1.1"));
        Assert.AreEqual("done", back.Status, back.Error);
        Assert.AreEqual(migrationsBefore, lab.Commands.Count(c => c.Args.Contains("migrate")), "Volver atrás el binario no corre migraciones.");
        Assert.AreEqual(new Version(0, 1, 1), n1.Host.Current().Version);
    }

    [TestMethod]
    public async Task ElActualizadorSeActualizaConElShaDelCanal_YGuardaElAnterior()
    {
        using var dir = new Folder();
        var binary = Path.Combine(dir.Path, "billing-oss-actualizador");
        await File.WriteAllTextAsync(binary, "viejo");
        var nuevo = Encoding.ASCII.GetBytes("binario nuevo");
        using var http = new HttpClient(new Stub(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(nuevo) }));
        var url = new Uri("https://canal.example/9.9.9/actualizador-linux-x64");

        var malo = new UpdaterBinary("9.9.9", url, new string('0', 64));
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<UpdateException>(() => SelfUpdate.Apply(malo, binary, http, CancellationToken.None))).Message, "SHA-256");
        Assert.AreEqual("viejo", await File.ReadAllTextAsync(binary));

        var bueno = malo with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(nuevo)) };
        Assert.IsTrue(SelfUpdate.IsNewer(bueno, UpdaterEngine.Own));
        Assert.IsTrue(await SelfUpdate.Apply(bueno, binary, http, CancellationToken.None));
        Assert.AreEqual("binario nuevo", await File.ReadAllTextAsync(binary));
        Assert.AreEqual("viejo", await File.ReadAllTextAsync(binary + ".anterior"));

        var http80 = malo with { Url = new Uri("http://canal.example/x") };
        await Assert.ThrowsExactlyAsync<UpdateException>(() => SelfUpdate.Apply(http80, binary, http, CancellationToken.None));
        Assert.IsFalse(SelfUpdate.IsNewer(bueno with { Version = UpdaterEngine.Own.Text() }, UpdaterEngine.Own));
    }

    [TestMethod]
    public void ComposeVersion_SoloImageDeServiciosDeLaReceta_PorDigest_YUnaVezEnComposeFile()
    {
        using var dir = new Folder();
        File.WriteAllText(Path.Combine(dir.Path, ".env"), "BILLING_VERSION=0.1.0\nCOMPOSE_FILE=compose.yaml:compose.postgres.yaml\n");
        var host = new ComposeHost(new UpdaterOptions { ComposeDirectory = dir.Path }, (_, _) => Task.FromResult(new CommandResult(0, string.Empty)));
        var images = new ReleaseImages
        {
            Nucleo = Digest("billing-oss", 1),
            Gotenberg = Digest("gotenberg", 1),
            Postgres = new Dictionary<string, string> { ["17"] = Digest("postgres17", 1), ["18"] = Digest("postgres18", 1) },
        };

        Assert.AreEqual(new Version(0, 1, 0), host.Current().Version);
        var mapped = ComposeHost.ImagesFor(["web", "worker", "migrate", "gotenberg", "postgres", "propio-del-proveedor"], images, postgresMajor: 17);
        CollectionAssert.AreEquivalent(new[] { "web", "worker", "migrate", "gotenberg", "postgres" }, mapped.Keys.ToArray());
        Assert.AreEqual(Digest("postgres17", 1), mapped["postgres"], "Nunca cambia de major.");

        host.Write(new Version(0, 2, 0), mapped);
        host.Write(new Version(0, 2, 0), mapped);
        Assert.AreEqual(new Version(0, 2, 0), host.Current().Version);
        CollectionAssert.AreEquivalent(mapped, host.Current().Images);
        Assert.Contains("COMPOSE_FILE=compose.yaml:compose.postgres.yaml:compose.version.yaml", File.ReadAllLines(Path.Combine(dir.Path, ".env")));

        // Un nombre o una imagen que podrían sumar otra clave al YAML no se escriben.
        Assert.ThrowsExactly<UpdateException>(() => host.Write(null, new Dictionary<string, string> { ["web\n    privileged"] = Digest("x", 1) }));
        Assert.ThrowsExactly<UpdateException>(() => host.Write(null, new Dictionary<string, string> { ["web"] = "billing-oss:latest" }));
        Assert.ThrowsExactly<UpdateException>(() => ComposeHost.ImagesFor(["web"], new ReleaseImages { Nucleo = "billing-oss:0.2.0" }, null));
    }

    [TestMethod]
    public void LaInstalacionDelActualizador_CoincideConElBinario()
    {
        var deploy = Path.Combine(RepoRoot(), "deploy");
        string Read(string relative) => File.ReadAllText(Path.Combine(deploy, relative));

        // La configuración de ejemplo se lee con las reglas del actualizador: una clave mal escrita no pasa.
        var options = UpdaterOptions.Load(Path.Combine(deploy, "actualizador", "actualizador.json.example"));
        Assert.AreEqual(("billnode01", null, "dump"), (options.Node, options.ChannelUrl, options.RecoveryMark));
        StringAssert.EndsWith(options.ConnectionStringFile!, "secrets/db_actualizador");

        // Si el binario nuevo no arranca, vuelve el que SelfUpdate dejó con el sufijo .anterior.
        var service = Read("actualizador/billing-oss-actualizador.service");
        StringAssert.Contains(service, "OnFailure=billing-oss-actualizador-anterior.service");
        StringAssert.Contains(service, "Restart=always");
        StringAssert.Contains(service, "ExecStart=/opt/billing-oss/actualizador/billing-oss-actualizador --config /etc/billing-oss/actualizador.json run");
        var back = Read("actualizador/billing-oss-actualizador-anterior.service");
        StringAssert.Contains(back, "b=/opt/billing-oss/actualizador/billing-oss-actualizador;");
        StringAssert.Contains(back, "mv -f \"$b.anterior\" \"$b\"");

        // La cadena del host, el puerto en 127.0.0.1 de la receta chica y los permisos del rol dueño.
        StringAssert.Contains(Read("compose/preparar.sh"), "secreto db_actualizador 0 ");
        StringAssert.Contains(Read("compose/compose.postgres.yaml"), "- 127.0.0.1:${BILLING_PG_PUERTO:-5432}:5432");
        StringAssert.Contains(Read("sql/roles.sql"), "GRANT EXECUTE ON FUNCTION pg_create_restore_point(text) TO billing_owner;");
        StringAssert.Contains(Read("sql/roles.sql"), "GRANT pg_monitor TO billing_owner;");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BillingOss.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No se encontró la raíz del repo.");
    }

    internal static string Digest(string name, int n) => $"registro.example/hornero/{name}@sha256:{n.ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0')}";

    internal static Release Release(string version, string? from = null, bool security = false)
    {
        var n = int.Parse(version.Replace(".", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return new Release
        {
            ReleaseVersion = version,
            From = from,
            Security = security,
            Severity = security ? "high" : null,
            Images = new ReleaseImages
            {
                Nucleo = Digest("billing-oss", n),
                Gotenberg = Digest("gotenberg", n),
                Postgres = new Dictionary<string, string> { ["18"] = Digest("postgres18", n) },
                Modules = new() { ["arca"] = System.Text.Json.JsonSerializer.SerializeToElement(Digest("billing-oss-arca", n)) },
            },
        };
    }

    internal static byte[] Channel(params Release[] releases)
    {
        var now = DateTimeOffset.UtcNow;
        return Encoding.UTF8.GetBytes(new ReleaseChannel
        {
            Format = 1,
            Name = "estable",
            Serial = now.ToUnixTimeSeconds(),
            Issued = now.AddHours(-1),
            Expires = now.AddDays(7),
            Releases = releases,
        }.ToJson());
    }

    internal static string Sign(ECDsa key, byte[] document) => Convert.ToBase64String(key.SignData(document, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Respuestas armadas por la prueba: ningún pedido sale a la red.</summary>
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("billing-oss-act-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal sealed record LabNode(string Name, string Dir, ComposeHost Host, UpdaterEngine Engine);

    /// <summary>
    /// Varios nodos sobre la misma base, cada uno con su carpeta de receta y su Docker falso. La salud de cada nodo la
    /// contesta el manejador HTTP: un nodo de <see cref="Unhealthy"/> no queda listo con esa versión.
    /// </summary>
    private sealed class Lab : IAsyncDisposable
    {
        private readonly CoreHarness core;
        private readonly ECDsa key;
        private readonly string recoveryMark;
        private readonly Folder root = new();
        private readonly NpgsqlDataSource db;
        private readonly HttpClient http;
        private readonly List<LabNode> nodes = [];

        public Lab(CoreHarness core, ECDsa key, string recoveryMark = "dump")
        {
            this.core = core;
            this.key = key;
            this.recoveryMark = recoveryMark;
            db = NpgsqlDataSource.Create(core.ConnectionString);
            http = new HttpClient(new Stub(Health));
            Marks = Path.Combine(root.Path, "marcas");
        }

        public ConcurrentQueue<Command> Commands { get; } = new();

        /// <summary>Salidas de migrate que fallan, en orden. Vacía, migrate anda.</summary>
        public ConcurrentQueue<string> MigrateFailures { get; } = new();

        public Dictionary<string, Version> Unhealthy { get; } = new(StringComparer.Ordinal);

        /// <summary>Nodos apagados: no dan vueltas.</summary>
        public HashSet<string> Off { get; } = new(StringComparer.Ordinal);

        public string Marks { get; }

        public LabNode Node(string name)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root.Path, name)).FullName;
            File.WriteAllText(Path.Combine(dir, ".env"), "BILLING_VERSION=0.1.0\nBILLING_PUBLIC_URL=https://billing.example.com\n");
            var options = new UpdaterOptions
            {
                Node = name,
                ComposeDirectory = dir,
                ConnectionString = core.ConnectionString,
                PublicKeys = [key.ExportSubjectPublicKeyInfoPem()],
                HealthUrl = new Uri($"http://{name}.test/health/ready"),
                SmokeUrls = [new Uri($"http://{name}.test/health/live")],
                ReadyTimeout = TimeSpan.FromSeconds(3),
                SoakPeriod = TimeSpan.Zero,
                RecoveryMark = recoveryMark,
                DumpDirectory = Marks,
                DiskPath = Path.GetPathRoot(dir)!,
                MinFreeBytes = 1,
                BinaryPath = null,
            };
            var host = new ComposeHost(options, (command, ct) => Docker(dir, command));
            var node = new LabNode(name, dir, host, new UpdaterEngine(options, db, host, http, TimeProvider.System, _ => { }));
            nodes.Add(node);
            return node;
        }

        /// <summary>Lo que hace check --file: el canal firmado con la llave de la prueba.</summary>
        public async Task Load(byte[] channel) =>
            Assert.IsNull(await nodes[0].Engine.Check(channel, Sign(key, channel), CancellationToken.None));

        /// <summary>Una vuelta de cada nodo, en orden.</summary>
        public async Task Round()
        {
            foreach (var node in nodes.Where(n => !Off.Contains(n.Name)))
            {
                await node.Engine.Tick(CancellationToken.None);
            }
        }

        public Task<long> Console(string version) => nodes[0].Engine.RequestFromConsole(version, "console:prueba", CancellationToken.None);

        public Task<UpdateRequest> RunUntilFinished(long id) => RunUntil(id, r => r.Status is "done" or "failed" or "rejected");

        /// <summary>Vueltas de todos los nodos encendidos hasta que el pedido cumpla la condición.</summary>
        public async Task<UpdateRequest> RunUntil(long id, Func<UpdateRequest, bool> until)
        {
            for (var round = 0; round < 40; round++)
            {
                await Round();
                var request = await core.Use<UpdateService, UpdateRequest?>(s => s.Find(id));
                if (request is not null && until(request))
                {
                    return request;
                }
            }

            Assert.Fail($"El pedido {id} no llegó a donde se esperaba.");
            return null!;
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await db.DisposeAsync();
            root.Dispose();
        }

        private Task<CommandResult> Docker(string dir, Command command)
        {
            Commands.Enqueue(command);
            var args = command.Args.Skip(1).ToList();
            CommandResult result = args switch
            {
                ["version", ..] => new(0, "27.3.1\n"),
                ["compose", "config", "--services"] => new(0, "arca\ngotenberg\nmigrate\nweb\nworker\n"),
                ["compose", "run", "--rm", "migrate"] => MigrateFailures.TryDequeue(out var failure) ? new(1, failure) : new(0, "Migraciones aplicadas."),
                ["compose", "exec", .., "pg_dump", "-Fc", "-d", _] => Dump(command.StdoutFile!),
                ["compose", "logs", ..] => new(0, "registro del nodo"),
                _ => new(0, string.Empty),
            };
            return Task.FromResult(result);
        }

        private static CommandResult Dump(string file)
        {
            File.WriteAllText(file, "PGDMP");
            return new(0, string.Empty);
        }

        private HttpResponseMessage Health(HttpRequestMessage request)
        {
            var node = nodes.Single(n => request.RequestUri!.Host == n.Name + ".test");
            var sick = Unhealthy.TryGetValue(node.Name, out var version) && node.Host.Current().Version == version;
            return new HttpResponseMessage(sick ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }
}
