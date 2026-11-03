using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BillingOss.Modules.Tests;

[TestClass]
public sealed class ModuleSdkTests
{
    /// <summary>Las pruebas de idempotencia corren con los dos registros: tienen que dar lo mismo.</summary>
    private const string Sqlite = "sqlite";
    private const string Postgres = "postgres";

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("secreto-de-prueba");
    private static readonly byte[] OldSecret = Encoding.UTF8.GetBytes("secreto-viejo");

    /// <summary>Tope de las esperas entre instancias: si la idempotencia se rompe, la prueba falla en vez de colgarse.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task Salud_SinFirma()
    {
        await using var m = await TestModule.Start(new ModuleDefinition([]));
        var res = await m.Client.GetAsync(new Uri("/v0/health", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [TestMethod]
    public async Task Manifiesto_SinFirmaDa401_YFirmadoDevuelveElManifiesto()
    {
        await using var m = await TestModule.Start(new ModuleDefinition(new JsonObject { ["id"] = "prueba" }));

        var (unsigned, _) = await m.Send(HttpMethod.Get, "/v0/manifest", Encoding.UTF8.GetBytes("otro"));
        var (signed, body) = await m.Send(HttpMethod.Get, "/v0/manifest", Secret);

        Assert.AreEqual(HttpStatusCode.Unauthorized, unsigned);
        Assert.AreEqual(HttpStatusCode.OK, signed);
        Assert.AreEqual("prueba", body!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task RelojCorrido_Da401()
    {
        await using var m = await TestModule.Start(new ModuleDefinition([]));
        var (status, _) = await m.Send(HttpMethod.Get, "/v0/manifest", Secret, timestamp: DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds());
        Assert.AreEqual(HttpStatusCode.Unauthorized, status);
    }

    [TestMethod]
    public async Task Rotacion_AceptaElSecretoViejo()
    {
        await using var m = await TestModule.Start(new ModuleDefinition([]), secrets: [Secret, OldSecret]);
        Assert.AreEqual(HttpStatusCode.OK, (await m.Send(HttpMethod.Get, "/v0/manifest", OldSecret)).Status);
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task MismaClave_UnSoloEfecto_YMismaRespuesta(string backend)
    {
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
        {
            runs++;
            return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["vmid"] = runs }));
        });
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(def, registry);

        var first = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1");
        var second = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1");

        Assert.AreEqual(1, runs);
        Assert.AreEqual(HttpStatusCode.OK, second.Status);
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    public async Task ClaveRepetidaEnOtraLinea_Da401SinEfecto()
    {
        // La firma cubre la primera línea y el registro leía las dos juntas: un pedido capturado se repetía con efecto.
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
        {
            runs++;
            return Task.FromResult(ModuleResponse.Completed());
        });
        await using var m = await TestModule.Start(def);

        await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1");
        var (status, body) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1", extraKey: "z");

        Assert.AreEqual(HttpStatusCode.Unauthorized, status);
        Assert.AreEqual(ErrorCodes.InvalidSignature, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task MismaClaveConOtraEntrada_Da409(string backend)
    {
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed()));
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(def, registry);

        await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "k");
        var (status, body) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "b" }), "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task ErrorReintentable_NoSeGuarda_ErrorDefinitivo_Si(string backend)
    {
        var runs = 0;
        var def = new ModuleDefinition([])
            .Operation("provisioning", "suspend", _ =>
            {
                runs++;
                return runs == 1
                    ? throw new ModuleException(ErrorCodes.UpstreamUnavailable, "caído")
                    : Task.FromResult(ModuleResponse.Completed());
            })
            .Operation("provisioning", "terminate", _ => throw new ModuleException(ErrorCodes.InvalidInput, "mal"));
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(def, registry);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await m.Send(HttpMethod.Post, "/v0/provisioning/suspend", Secret, Envelope([]), "s")).Status);
        Assert.AreEqual(HttpStatusCode.OK, (await m.Send(HttpMethod.Post, "/v0/provisioning/suspend", Secret, Envelope([]), "s")).Status);
        Assert.AreEqual(2, runs);

        var first = await m.Send(HttpMethod.Post, "/v0/provisioning/terminate", Secret, Envelope([]), "t");
        var second = await m.Send(HttpMethod.Post, "/v0/provisioning/terminate", Secret, Envelope([]), "t");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, first.Status);
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task ElRegistroSobreviveAUnReinicio(string backend)
    {
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
        {
            runs++;
            return Task.FromResult(ModuleResponse.Completed());
        });
        await using var registry = await Registry.Create(backend);
        await using (var m = await TestModule.Start(def, registry))
        {
            await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        }

        await using (var m = await TestModule.Start(def, registry))
        {
            await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        }

        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task LaRespuestaRepetidaNoTraeInstanceState(string backend)
    {
        var def = new ModuleDefinition([]).Operation("fiscal", "authorize", _ =>
            Task.FromResult(ModuleResponse.Completed(new JsonObject { ["cae"] = "1" }, new JsonObject { ["ticket"] = "t" })));
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(def, registry);

        var first = await m.Send(HttpMethod.Post, "/v0/fiscal/authorize", Secret, Envelope([]), "k");
        var second = await m.Send(HttpMethod.Post, "/v0/fiscal/authorize", Secret, Envelope([]), "k");

        Assert.IsNotNull(first.Body!["instance_state"]);
        Assert.IsNull(second.Body!["instance_state"]);
        Assert.AreEqual("1", second.Body!["result"]!["cae"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task DosInstancias_RegistroCompartido_UnSoloEfecto(string backend)
    {
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
            Task.FromResult(ModuleResponse.Completed(new JsonObject { ["vmid"] = Interlocked.Increment(ref runs) })));
        await using var registry = await Registry.Create(backend);
        await using var a = await TestModule.Start(def, registry);
        await using var b = await TestModule.Start(def, registry);

        var first = await a.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1");
        var second = await b.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1");
        var other = await b.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "b" }), "svc-1-create-1");

        Assert.AreEqual(1, runs);
        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, other.Body!["error"]!["code"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task DosInstanciasALaVez_ElMismoPedidoSeEjecutaUnaSolaVez(string backend)
    {
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", async _ =>
        {
            var n = Interlocked.Increment(ref runs);
            await Task.Delay(300);
            return ModuleResponse.Completed(new JsonObject { ["vmid"] = n });
        });
        await using var registry = await Registry.Create(backend);

        // Arrancan a la vez, como dos réplicas después de un despliegue: crear la tabla no puede fallar en ninguna.
        var started = await Task.WhenAll(Task.Run(() => TestModule.Start(def, registry)), Task.Run(() => TestModule.Start(def, registry)));
        await using var a = started[0];
        await using var b = started[1];

        var calls = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? a : b).Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1")));
        var after = await b.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope(new() { ["plan"] = "a" }), "svc-1-create-1");

        Assert.AreEqual(1, runs);
        foreach (var (status, body) in calls)
        {
            if (status == HttpStatusCode.OK)
            {
                Assert.AreEqual(1, body!["result"]!["vmid"]!.GetValue<int>());
            }
            else
            {
                // La que llega mientras otra ejecuta recibe un 409 reintentable, nunca una segunda ejecución.
                Assert.AreEqual(HttpStatusCode.Conflict, status);
                Assert.AreEqual(ErrorCodes.Conflict, body!["error"]!["code"]!.GetValue<string>());
                Assert.IsTrue(body["error"]!["retryable"]!.GetValue<bool>());
            }
        }

        Assert.AreEqual(1, after.Body!["result"]!["vmid"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task EnCursoEnUnaInstancia_LaOtraDa409_YDespuesDevuelveLoGuardado(string backend)
    {
        var runs = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var def = new ModuleDefinition([]).Operation("payment", "refund", async _ =>
        {
            var n = Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await gate.Task.WaitAsync(Wait);
            return ModuleResponse.Completed(new JsonObject { ["refund_id"] = $"r{n}" });
        });
        await using var registry = await Registry.Create(backend);
        await using var a = await TestModule.Start(def, registry);
        await using var b = await TestModule.Start(def, registry);

        var slow = a.Send(HttpMethod.Post, "/v0/payment/refund", Secret, Envelope([]), "pay-9-refund-1");
        await entered.Task.WaitAsync(Wait);
        var during = await b.Send(HttpMethod.Post, "/v0/payment/refund", Secret, Envelope([]), "pay-9-refund-1");
        gate.SetResult();
        var done = await slow;
        var after = await b.Send(HttpMethod.Post, "/v0/payment/refund", Secret, Envelope([]), "pay-9-refund-1");

        Assert.AreEqual(HttpStatusCode.Conflict, during.Status);
        Assert.AreEqual(5, during.Body!["error"]!["retry_after"]!.GetValue<int>());
        Assert.AreEqual(1, runs);
        Assert.AreEqual(done.Body!.ToJsonString(), after.Body!.ToJsonString());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task TomaAbandonada_LaRetomaOtraInstancia_YLaViejaNoPisaSuRespuesta(string backend)
    {
        // La instancia A se cuelga con la clave tomada. Pasado StaleRun (un segundo, por tiempo y sin tocar la fila), B la
        // retoma con una toma nueva. Cuando A despierta, su respuesta no pisa la de B: la fila ya no es de su toma.
        var runs = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var def = new ModuleDefinition([]).Operation("provisioning", "create", async _ =>
        {
            var n = Interlocked.Increment(ref runs);
            if (n == 1)
            {
                entered.TrySetResult();
                await gate.Task.WaitAsync(Wait);
            }

            return ModuleResponse.Completed(new JsonObject { ["vmid"] = n });
        });
        await using var registry = await Registry.Create(backend);
        var stale = TimeSpan.FromSeconds(1);
        await using var a = await TestModule.Start(def, registry, staleRun: stale);
        await using var b = await TestModule.Start(def, registry, staleRun: stale);

        var hung = a.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        await entered.Task.WaitAsync(Wait);
        var first = await registry.StartedAt("k");

        // SQLite guarda la toma en segundos: se espera a que cambie el segundo, además de que venza StaleRun.
        await Task.Delay(stale + TimeSpan.FromMilliseconds(1500));
        var retaken = await b.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        var second = await registry.StartedAt("k");
        gate.SetResult();
        await hung;
        var after = await a.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(2, runs);
        Assert.AreNotEqual(first, second, "La retoma de B tiene su propia hora de toma.");
        Assert.AreEqual(2, retaken.Body!["result"]!["vmid"]!.GetValue<int>());
        Assert.AreEqual(retaken.Body!.ToJsonString(), after.Body!.ToJsonString());
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task Listo_SinFirma_ConsultaElRegistro(string backend)
    {
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(new ModuleDefinition([]), registry);

        using var res = await m.Client.GetAsync(new Uri("/v0/ready", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [TestMethod]
    public async Task Listo_ConElRegistroInaccesible_Da503_YVivoSigueEn200()
    {
        // Una réplica que no llega a su registro contestaría 500 a toda operación con efecto: el balanceador la tiene que sacar.
        await using var registry = await Registry.Create(Sqlite);
        await using var m = await TestModule.Start(new ModuleDefinition([]), registry);
        registry.Block();

        using var ready = await m.Client.GetAsync(new Uri("/v0/ready", UriKind.Relative));
        using var live = await m.Client.GetAsync(new Uri("/v0/health", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
    }

    [TestMethod]
    public async Task ElRegistroEnArchivo_CreaSuCarpeta()
    {
        // Un módulo que copia el ejemplo del README con data/idempotency.db arranca aunque la carpeta no exista.
        var root = Path.Combine(Path.GetTempPath(), $"idem-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "anidada", "idempotency.db");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            await using var app = builder.Build();
            app.MapModule(new ModuleDefinition([]), new ModuleSdkOptions { SigningSecrets = [Secret], IdempotencyStorePath = path });

            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnErrorPuedeLlevarElEstadoDeLaInstancia_YLaRespuestaGuardadaSaleSinEl()
    {
        // Un ticket obtenido en una llamada que después falla viaja en el error, para que el núcleo lo guarde. El registro
        // de idempotencia no lo guarda: la repetición del error definitivo sale sin él, como toda respuesta repetida.
        var def = new ModuleDefinition([]).Operation("fiscal", "authorize", _ =>
            throw new ModuleException(ErrorCodes.Rejected, "El fisco lo rechazó") { InstanceState = new JsonObject { ["ticket"] = "T-1" } });
        await using var m = await TestModule.Start(def);

        var first = await m.Send(HttpMethod.Post, "/v0/fiscal/authorize", Secret, Envelope([]), "k");
        var replay = await m.Send(HttpMethod.Post, "/v0/fiscal/authorize", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, first.Status);
        Assert.AreEqual("T-1", first.Body!["instance_state"]!["ticket"]!.GetValue<string>());
        Assert.AreEqual(ErrorCodes.Rejected, replay.Body!["error"]!["code"]!.GetValue<string>());
        Assert.IsNull(replay.Body["instance_state"]);
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task RegistroCaidoDespuesDelEfecto_LaRespuestaSaleIgual(string backend)
    {
        await using var registry = await Registry.Create(backend);
        var def = new ModuleDefinition([]).Operation("provisioning", "create", async _ =>
        {
            await registry.Break();
            return ModuleResponse.Completed(new JsonObject { ["vmid"] = 100 });
        });
        await using var m = await TestModule.Start(def, registry);

        var (status, body) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(100, body!["result"]!["vmid"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task TomaConfirmadaSoloEnLaPrimaria_NoEjecuta_AunqueElServidorHableOtroIdioma()
    {
        // PostgreSQL avisa con un WARNING en el COMMIT cuando cancela la espera de la réplica sincrónica y confirma solo en
        // la primaria. El campo Severity del aviso viene traducido según lc_messages del servidor (WARNUNG en alemán), así
        // que la SDK mira el campo invariante. Acá el aviso lo produce un disparador diferido en la toma de la clave: la
        // SDK no ejecuta y contesta conflict reintentable para que el núcleo vuelva a intentar.
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["vmid"] = 1 }));
        });
        await using var registry = await Registry.Create(Postgres);
        await registry.SpeakIn("de_DE");
        await using var m = await TestModule.Start(def, registry);
        await registry.WarnOnClaim();

        var (status, body) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(5, body!["error"]!["retry_after"]!.GetValue<int>());
        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    public async Task LaCadenaDePostgresSeTomaDeLaConfiguracion()
    {
        // Ningún Program.cs tiene que pasarla: con Module__IdempotencyConnectionString puesta, todo módulo de la SDK la usa.
        await using var registry = await Registry.Create(Postgres);
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed()));
        await using var m = await TestModule.Start(def, registry, fromConfiguration: true);

        var (status, _) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("done", await registry.Scalar<string>("SELECT state FROM module_idempotency WHERE key = $1", "k"));
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task FallaSinResultadoConocido_DejaLaClaveEnCurso(string backend)
    {
        // El efecto ocurrió y el manejador tiró después: el reintento inmediato en la otra instancia no lo repite.
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("después del efecto");
        });
        await using var registry = await Registry.Create(backend);
        await using var a = await TestModule.Start(def, registry);
        await using var b = await TestModule.Start(def, registry);

        var first = await a.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        var retry = await b.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.InternalServerError, first.Status);
        Assert.AreEqual(HttpStatusCode.Conflict, retry.Status);
        Assert.AreEqual(ErrorCodes.Conflict, retry.Body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task ErrorReintentable_SueltaLaClave(string backend)
    {
        // El módulo sabe que el pedido no llegó a ejecutarse: el reintento vuelve a ejecutar.
        var runs = 0;
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
            ++runs == 1
                ? throw new ModuleException(ErrorCodes.UpstreamUnavailable, "sin respuesta", retryAfter: 1)
                : Task.FromResult(ModuleResponse.Completed()));
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(def, registry);

        var first = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        var retry = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, first.Status);
        Assert.AreEqual(HttpStatusCode.OK, retry.Status);
        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    [DataRow(Sqlite)]
    [DataRow(Postgres)]
    public async Task LasCredencialesGuardadasVanCifradas(string backend)
    {
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
            Task.FromResult(ModuleResponse.Completed(new JsonObject { ["vmid"] = 7, ["secrets"] = new JsonObject { ["root_password"] = "clave-generada-afuera" } })));
        await using var registry = await Registry.Create(backend);
        await using var m = await TestModule.Start(def, registry, secrets: [Secret, OldSecret]);

        var first = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        var stored = Encoding.UTF8.GetString(await registry.Scalar<byte[]>(
            backend == Postgres ? "SELECT response FROM module_idempotency WHERE key = $1" : "SELECT response FROM idempotency WHERE key = $key", "k"));
        var second = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual("clave-generada-afuera", first.Body!["result"]!["secrets"]!["root_password"]!.GetValue<string>());
        StringAssert.DoesNotMatch(stored, new System.Text.RegularExpressions.Regex("clave-generada-afuera|root_password"));
        StringAssert.Contains(stored, "sealed:v1:");
        Assert.AreEqual("clave-generada-afuera", second.Body!["result"]!["secrets"]!["root_password"]!.GetValue<string>());
        Assert.AreEqual(7, second.Body!["result"]!["vmid"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task LasCredencialesCifradasConUnSecretoRetirado_SalenSinEllas()
    {
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ =>
            Task.FromResult(ModuleResponse.Completed(new JsonObject { ["vmid"] = 7, ["secrets"] = new JsonObject { ["root_password"] = "x" } })));
        await using var registry = await Registry.Create(Sqlite);
        await using (var m = await TestModule.Start(def, registry, secrets: [OldSecret]))
        {
            await m.Send(HttpMethod.Post, "/v0/provisioning/create", OldSecret, Envelope([]), "k");
        }

        // Terminó la rotación: el secreto viejo ya no está. La respuesta repetida sigue valiendo, sin las credenciales.
        await using (var m = await TestModule.Start(def, registry, secrets: [Secret]))
        {
            var (status, body) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(7, body!["result"]!["vmid"]!.GetValue<int>());
            Assert.IsNull(body["result"]!["secrets"]);
        }
    }

    [TestMethod]
    public async Task ElManejadorSigueAunqueElNucleoCorte_YVenceConElPlazoDelManifiesto()
    {
        // El token del manejador no es el del pedido HTTP: lo cancela el plazo de la operación (dos veces el del
        // manifiesto). Mientras tanto la clave sigue en curso, y al vencer la respuesta es outcome_unknown.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var def = new ModuleDefinition(new JsonObject { ["timeouts"] = new JsonObject { ["create"] = 1 } })
            .Operation("provisioning", "create", async call =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Aborted);
                return ModuleResponse.Completed();
            });
        await using var registry = await Registry.Create(Sqlite);
        await using var m = await TestModule.Start(def, registry);

        var slow = m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        await started.Task.WaitAsync(Wait);
        var meanwhile = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");
        var (status, body) = await slow;

        Assert.AreEqual(HttpStatusCode.Conflict, meanwhile.Status);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.Conflict, (await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k")).Status);
    }

    [TestMethod]
    public async Task SinClaveODesconocida()
    {
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ => Task.FromResult(ModuleResponse.Completed()));
        await using var m = await TestModule.Start(def);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]))).Status);
        Assert.AreEqual(HttpStatusCode.NotImplemented, (await m.Send(HttpMethod.Post, "/v0/provisioning/resize", Secret, Envelope([]), "k")).Status);
    }

    [TestMethod]
    public async Task ErrorInterno_NoDevuelveElDetalle()
    {
        var def = new ModuleDefinition([]).Operation("provisioning", "create", _ => throw new InvalidOperationException("token-secreto"));
        await using var m = await TestModule.Start(def);

        var (status, body) = await m.Send(HttpMethod.Post, "/v0/provisioning/create", Secret, Envelope([]), "k");

        Assert.AreEqual(HttpStatusCode.InternalServerError, status);
        Assert.DoesNotContain("token-secreto", body!.ToJsonString());
    }

    [TestMethod]
    public async Task Uso_DevuelveLasLecturasPedidasComoTexto_SinClaveDeIdempotencia()
    {
        IReadOnlyList<string>? asked = null;
        var def = new ModuleDefinition(UsageManifest()).Usage((_, metrics) =>
        {
            asked = metrics;
            return Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>
            {
                ["transfer_gb"] = 1834.250m,
                ["disk_gb"] = 12.1234567m,
                ["cpu_hours"] = 3m,
            });
        });
        await using var m = await TestModule.Start(def);

        var (status, body) = await m.Send(HttpMethod.Post, UsageProtocol.Path, Secret, Envelope(new() { ["metrics"] = new JsonArray("transfer_gb", "disk_gb") }));

        Assert.AreEqual(HttpStatusCode.OK, status);
        CollectionAssert.AreEqual(new[] { "transfer_gb", "disk_gb" }, asked!.ToArray());
        var metrics = body!["result"]!["metrics"]!.AsObject();
        Assert.AreEqual("1834.25", metrics["transfer_gb"]!.GetValue<string>());
        Assert.AreEqual("12.123457", metrics["disk_gb"]!.GetValue<string>());
        Assert.IsFalse(metrics.ContainsKey("cpu_hours"), "Lo que no se pidió no va.");
    }

    [TestMethod]
    public async Task Uso_LecturaNegativaOSinLista_DaErrorYSinCapacidadNoSeRegistra()
    {
        var def = new ModuleDefinition(UsageManifest()).Usage((_, _) =>
            Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal> { ["disk_gb"] = -1m }));
        await using var m = await TestModule.Start(def);

        var negative = await m.Send(HttpMethod.Post, UsageProtocol.Path, Secret, Envelope(new() { ["metrics"] = new JsonArray("disk_gb") }));
        var missing = await m.Send(HttpMethod.Post, UsageProtocol.Path, Secret, Envelope([]));

        Assert.AreEqual(HttpStatusCode.InternalServerError, negative.Status);
        Assert.AreEqual(ErrorCodes.Internal, negative.Body!["error"]!["code"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, missing.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => new ModuleDefinition(new JsonObject { ["id"] = "sin-uso" })
            .Usage((_, _) => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>())));
    }

    [TestMethod]
    [DataRow("12.5", true)]
    [DataRow("0", true)]
    [DataRow("999999999999.999999", true)]
    [DataRow("1000000000000", false)]
    [DataRow("1.1234567", false)]
    [DataRow("-1", false)]
    [DataRow("1e3", false)]
    [DataRow("1,5", false)]
    [DataRow(".5", false)]
    [DataRow("", false)]
    public void Uso_LecturasValidas(string text, bool valid) =>
        Assert.AreEqual(valid, UsageProtocol.TryParse(text, out _));

    [TestMethod]
    public void Uso_ElManifiestoDeclaraTipoUnidadYEtiqueta_YDescartaLoMalFormado()
    {
        var manifest = UsageManifest();
        manifest["usage_metrics"]!.AsArray().Add(new JsonObject { ["id"] = "raro", ["kind"] = "histogram" });

        var declared = UsageProtocol.Declared(manifest);

        Assert.IsTrue(UsageProtocol.Declares(manifest));
        Assert.HasCount(3, declared);
        Assert.AreEqual(UsageProtocol.Counter, declared["transfer_gb"].Kind);
        Assert.AreEqual("GB", declared["disk_gb"].Unit);
        Assert.AreEqual("Transfer", declared["transfer_gb"].Label["en"]);
    }

    private static JsonObject UsageManifest() => new()
    {
        ["id"] = "medido",
        ["capabilities"] = new JsonArray("provisioning.core", UsageProtocol.Capability),
        ["usage_metrics"] = new JsonArray(
            new JsonObject { ["id"] = "transfer_gb", ["kind"] = UsageProtocol.Counter, ["unit"] = "GB", ["label"] = new JsonObject { ["es"] = "Transferencia", ["en"] = "Transfer" } },
            new JsonObject { ["id"] = "disk_gb", ["kind"] = UsageProtocol.Gauge, ["unit"] = "GB" },
            new JsonObject { ["id"] = "cpu_hours", ["kind"] = UsageProtocol.Counter, ["unit"] = "h" }),
    };

    private static JsonObject Envelope(JsonObject input) => new()
    {
        ["instance"] = new JsonObject { ["id"] = "inst-1", ["config"] = new JsonObject() },
        ["context"] = new JsonObject(),
        ["input"] = input,
    };

    /// <summary>
    /// Registro de idempotencia que comparten las instancias de una prueba: un archivo SQLite, o una base PostgreSQL
    /// creada para la prueba sobre BILLINGOSS_TEST_PG. Sin esa variable, las pruebas con PostgreSQL se saltean.
    /// </summary>
    private sealed class Registry : IAsyncDisposable
    {
        private string? path;
        private string? admin;
        private string? database;
        private string? pg;

        public static async Task<Registry> Create(string backend)
        {
            if (backend == Sqlite)
            {
                return new Registry { path = Path.Combine(Path.GetTempPath(), $"idem-{Guid.NewGuid():N}.db") };
            }

            var env = Environment.GetEnvironmentVariable("BILLINGOSS_TEST_PG")
                ?? throw new AssertInconclusiveException("Falta BILLINGOSS_TEST_PG: se saltean las pruebas de integración.");

            // Todas las conexiones van con pool, como en producción: sin pool cada una es un proceso nuevo del servidor.
            // El pool de la base se cierra antes de borrarla.
            var admin = new NpgsqlConnectionStringBuilder(env) { Timeout = 60 }.ConnectionString;
            var name = $"billing_oss_t_{DateTime.UtcNow:yyMMddHHmm}_{Guid.NewGuid():N}"[..33];
            await using (var db = new NpgsqlConnection(admin))
            {
                await db.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", db) { CommandTimeout = 300 };
                await cmd.ExecuteNonQueryAsync();
            }

            return new Registry
            {
                admin = admin,
                database = name,
                pg = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString,
            };
        }

        public ModuleSdkOptions Options(IReadOnlyList<byte[]> secrets, TimeSpan? staleRun = null) =>
            new() { SigningSecrets = secrets, IdempotencyStorePath = path, IdempotencyConnectionString = pg, StaleRun = staleRun ?? TimeSpan.FromMinutes(10) };

        /// <summary>La hora de la toma en curso de una clave, tal como la guarda el registro.</summary>
        public Task<object> StartedAt(string key) =>
            Scalar<object>(pg is null ? "SELECT started_at FROM idempotency WHERE key = $key" : "SELECT started_at FROM module_idempotency WHERE key = $1", key);

        /// <summary>Deja el registro SQLite inaccesible: el archivo pasa a ser un directorio y abrirlo falla.</summary>
        public void Block()
        {
            File.Delete(path!);
            Directory.CreateDirectory(path!);
        }

        public string? ConnectionString => pg;

        public async Task<T> Scalar<T>(string sql, string key)
        {
            if (path is not null)
            {
                await using var lite = new SqliteConnection($"Data Source={path};Pooling=False");
                await lite.OpenAsync();
                await using var liteCmd = lite.CreateCommand();
                liteCmd.CommandText = sql;
                liteCmd.Parameters.AddWithValue("$key", key);
                return (T)(await liteCmd.ExecuteScalarAsync())!;
            }

            await using var db = new NpgsqlConnection(pg);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, db);
            cmd.Parameters.AddWithValue(key);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        /// <summary>El servidor habla en otro idioma: la severidad de los avisos llega traducida (WARNUNG, AVISO). Solo PostgreSQL.</summary>
        public Task SpeakIn(string locale) => Sql($"ALTER DATABASE {database} SET lc_messages = '{locale}'", null);

        /// <summary>Un disparador diferido avisa en el COMMIT de cada toma, como hace PostgreSQL al cancelar la espera de la réplica. Solo PostgreSQL.</summary>
        public async Task WarnOnClaim()
        {
            await Sql("""
                CREATE FUNCTION prueba_aviso() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE WARNING 'canceling wait for synchronous replication due to user request'; RETURN NULL; END $$
                """, null);
            await Sql("CREATE CONSTRAINT TRIGGER prueba_aviso AFTER INSERT ON module_idempotency DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION prueba_aviso()", null);
        }

        /// <summary>Rompe el registro, como una base que deja de responder.</summary>
        public Task Break() => pg is null
            ? Sql("ALTER TABLE idempotency RENAME TO roto", null)
            : Sql("ALTER TABLE module_idempotency RENAME TO roto", null);

        public async ValueTask DisposeAsync()
        {
            if (path is not null)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path);
                }
                else
                {
                    File.Delete(path);
                }

                return;
            }

            await using (var own = new NpgsqlConnection(pg))
            {
                NpgsqlConnection.ClearPool(own);
            }

            await using var db = new NpgsqlConnection(admin);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", db) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task Sql(string sql, string? key)
        {
            if (path is not null)
            {
                await using var lite = new SqliteConnection($"Data Source={path};Pooling=False");
                await lite.OpenAsync();
                await using var liteCmd = lite.CreateCommand();
                liteCmd.CommandText = sql;
                if (key is not null)
                {
                    liteCmd.Parameters.AddWithValue("$key", key);
                }

                await liteCmd.ExecuteNonQueryAsync();
                return;
            }

            await using var db = new NpgsqlConnection(pg);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, db);
            if (key is not null)
            {
                cmd.Parameters.AddWithValue(key);
            }

            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestModule(WebApplication app, Registry? owned) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestClient();

        public static async Task<TestModule> Start(ModuleDefinition def, Registry? registry = null, byte[][]? secrets = null, bool fromConfiguration = false, TimeSpan? staleRun = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            if (fromConfiguration)
            {
                builder.Configuration["Module:IdempotencyConnectionString"] = registry!.ConnectionString;
            }

            var app = builder.Build();
            var owned = registry is null ? await Registry.Create(Sqlite) : null;
            app.MapModule(def, fromConfiguration
                ? new ModuleSdkOptions { SigningSecrets = secrets ?? [Secret] }
                : (registry ?? owned)!.Options(secrets ?? [Secret], staleRun));
            await app.StartAsync();
            return new TestModule(app, owned);
        }

        /// <summary><paramref name="extraKey"/> va como segunda línea de Idempotency-Key, fuera de la firma: así repite un pedido capturado.</summary>
        public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(
            HttpMethod method,
            string path,
            byte[] secret,
            JsonObject? body = null,
            string? key = null,
            long? timestamp = null,
            string? extraKey = null)
        {
            var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString());
            var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestId = Guid.NewGuid().ToString();
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = new ByteArrayContent(bytes) };
            request.Headers.Add(ModuleHeaders.RequestId, requestId);
            request.Headers.Add(ModuleHeaders.Timestamp, ts.ToString(CultureInfo.InvariantCulture));
            if (key is not null)
            {
                request.Headers.Add(ModuleHeaders.IdempotencyKey, key);
            }

            if (extraKey is not null)
            {
                request.Headers.Add(ModuleHeaders.IdempotencyKey, extraKey);
            }

            request.Headers.Add(ModuleHeaders.Signature, ModuleSignature.Compute(secret, ts, method.Method, path, requestId, key, bytes));
            using var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }
}
