using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;
using Microsoft.AspNetCore.Http;

namespace BillingOss.Module.Proxmox.Tests;

/// <summary>Errores del protocolo (sección 10), firma, idempotencia y secretos que no se filtran (escenarios 2, 3, 5 y 7).</summary>
[TestClass]
public sealed class ErrorTests
{
    [TestMethod]
    public async Task Un403DeProxmox_EsInvalidConfig()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.Hook = (ctx, route) => Fail(ctx, route, "GET /cluster/resources", 403, "Permission check failed (/vms, VM.Audit)");

        var (status, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" }));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidConfig, ModuleHost.Code(body));
        Assert.AreEqual("permission_denied", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task UnaVmBloqueada_EsConflictReintentable()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Vm(7001)!.Config["lock"] = "backup";

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.Conflict, ModuleHost.Code(body));
        Assert.AreEqual("vm_locked", ModuleHost.Reason(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsNotNull(body["error"]!["retry_after"]);
    }

    [TestMethod]
    [DataRow("/v0/provisioning/suspend", "stopped")]
    [DataRow("/v0/provisioning/unsuspend", "running")]
    public async Task ConLockDeBackup_SeContestaConflictSinLanzarLaTarea(string path, string initial)
    {
        // Proxmox acepta stop o start con la VM bloqueada y la tarea falla adentro: el módulo mira el lock antes.
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        var vm = host.Pve.Vm(7001)!;
        vm.Status = initial == "stopped" ? "running" : "stopped";
        vm.Config["lock"] = "backup";
        var before = host.Pve.Calls.Count;

        var (status, body) = await host.Send(path, [], context, "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.AreEqual("vm_locked", ModuleHost.Reason(body));
        Assert.IsFalse(host.Pve.Calls.Skip(before).Any(c => c.Contains("/status/st", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("/v0/provisioning/suspend", "qmstop")]
    [DataRow("/v0/provisioning/unsuspend", "qmstart")]
    public async Task UnaTareaQueNoConsigueElLock_EsConflict_NoUnRechazo(string path, string task)
    {
        // El lock lo tomó otra tarea entre la lectura de la configuración y el pedido: la tarea termina con error.
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        if (task == "qmstart")
        {
            host.Pve.Vm(7001)!.Status = "stopped";
        }

        host.Pve.FailNextTask = task;
        host.Pve.FailNextTaskExit = "can't lock file '/var/lock/qemu-server/lock-7001.conf' - got timeout";

        var (status, body) = await host.Send(path, [], context, "k");

        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.AreEqual("vm_locked", ModuleHost.Reason(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task UnNodoConMasDe497DiasEncendido_SusTareasSeReconocen()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.LongUptime = true;

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "k");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("stopped", host.Pve.Vm(7001)!.Status);
    }

    [TestMethod]
    public async Task SinConexionATiempo_EnUnCambio_EsUpstreamUnavailable()
    {
        // El tiempo para conectar del handler vence con una cancelación: el pedido no salió.
        var config = InstanceConfig.Read(new JsonObject
        {
            ["api_url"] = "https://pve.example.test:8006",
            ["token_id"] = "billing@pve!modulo",
            ["token_secret"] = "secreto-de-prueba",
            ["network_pool"] = ModuleHost.Pool(),
        });
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            ConnectCallback = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("No se llega nunca.");
            },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var pve = new Pve(config, http, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System, TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsExactlyAsync<BillingOss.Modules.Sdk.ModuleException>(() => pve.Post("/nodes/pve1/qemu/7001/status/stop"));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, error.Code);
    }

    [TestMethod]
    public async Task SinConexionConProxmox_EsUpstreamUnavailable()
    {
        await using var host = await ModuleHost.Start();
        var config = host.Config();
        config["api_url"] = $"https://127.0.0.1:{ClosedPort().ToString(CultureInfo.InvariantCulture)}";

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" }), "k", config);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task UnCorteDespuesDeEnviarUnCambio_EsOutcomeUnknown_YLaRepeticionTermina()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        host.Pve.Hook = (ctx, route) =>
        {
            if (route != "PUT /nodes/pve1/qemu/7001/config")
            {
                return Task.FromResult(false);
            }

            ctx.Abort();
            return Task.FromResult(true);
        };

        var (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "svc-7001-suspend-1");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, ModuleHost.Code(body));

        host.Pve.Hook = null;
        (status, body) = await host.Send("/v0/provisioning/suspend", [], context, "svc-7001-suspend-1");
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("0", host.Pve.Vm(7001)!.Config["onboot"]);
    }

    [TestMethod]
    public async Task UnaRespuestaSinData_EsUpstreamUnavailable()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.Hook = async (ctx, route) =>
        {
            if (route != "GET /cluster/resources")
            {
                return false;
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"otra":"cosa"}""");
            return true;
        };

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" }));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task UnaRespuestaConErrors_SeRechaza()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.Hook = async (ctx, route) =>
        {
            if (route != "GET /cluster/resources")
            {
                return false;
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"data":[],"errors":{"type":"value 'x' does not have a value in the enumeration"}}""");
            return true;
        };

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" }));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task UnInventarioConUnNodoInvalido_NoArmaRutas()
    {
        await using var host = await ModuleHost.Start();
        var vm = host.Pve.AddVm(7001, "vm7001");
        vm.Node = "../pve1";

        var (_, body) = await host.Send("/v0/provisioning/get_status", [], ModuleHost.Context(ids: new JsonObject { ["vmid"] = "7001" }));

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
        Assert.IsFalse(host.Pve.Calls.Any(c => c.Contains("..", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FirmaInvalida_Da401()
    {
        await using var host = await ModuleHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v0/provisioning/get_status", UriKind.Relative))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ModuleHeaders.RequestId, Guid.NewGuid().ToString());
        request.Headers.Add(ModuleHeaders.Timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ModuleHeaders.Signature, "v0=" + new string('0', 64));

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(host.Pve.Calls);
    }

    [TestMethod]
    public async Task MismaClaveConOtraEntrada_EsIdempotencyConflict()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);

        await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "stop" }, context, "accion-1");
        var (status, body) = await host.Send("/v0/provisioning/run_action", new JsonObject { ["action"] = "start" }, context, "accion-1");

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, ModuleHost.Code(body));
    }

    [TestMethod]
    public async Task ManualReview_Es409_NoReintentable_YSeRepiteIgual()
    {
        await using var host = await ModuleHost.Start();
        host.Pve.AddVm(7001, "vm7001", description: "otra");

        var first = await host.Create();
        var second = await host.Create();

        Assert.AreEqual(HttpStatusCode.Conflict, first.Status);
        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(first.Body));
        Assert.IsFalse(first.Body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.AreEqual(first.Body.ToJsonString(), second.Body!.ToJsonString());
        Assert.AreEqual(409, ErrorCodes.HttpStatus(ErrorCodes.ManualReview));
        Assert.IsFalse(ErrorCodes.IsRetryable(ErrorCodes.ManualReview));
    }

    [TestMethod]
    public async Task LosSecretos_NoAparecenEnRespuestasErroresNiLogs()
    {
        await using var host = await ModuleHost.Start();
        var context = await LifecycleTests.Provisioned(host);
        await host.Send("/v0/provisioning/sso", [], context);
        await host.Send("/v0/provisioning/suspend", [], context, "s1");
        await host.Send("/v0/provisioning/view", [], context);

        // Proxmox repite el token en el motivo de un error: el módulo lo tapa.
        host.Pve.Hook = (ctx, route) => Fail(ctx, route, "GET /cluster/resources", 500, $"bad token {host.Pve.Secret} from 192.0.2.9");
        var (_, failed) = await host.Send("/v0/provisioning/get_status", [], context);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(failed));
        host.Pve.Hook = (ctx, route) => Fail(ctx, route, "PUT /nodes/pve1/qemu/7001/config", 400, $"parameter verification failed {host.Pve.Secret} ip 192.0.2.9");
        var (_, rejected) = await host.Send("/v0/provisioning/unsuspend", [], context, "u1");
        Assert.AreEqual(ErrorCodes.Rejected, ModuleHost.Code(rejected));

        // Configuración con un secreto mal puesto: el error nombra el campo, no el valor.
        var config = host.Config();
        config["api_url"] = "http://" + host.Pve.Secret;
        await host.Send("/v0/instance/test", [], [], config: config);

        var everything = string.Join('\n', host.Responses) + '\n' + host.Logs.All;
        foreach (var secret in new[] { host.Pve.Secret, host.Pve.ConsoleSecret, host.Password })
        {
            Assert.DoesNotContain(secret, everything);
        }

        Assert.DoesNotContain("192.0.2.9", everything);
    }

    private static async Task<bool> Fail(HttpContext ctx, string route, string target, int status, string reason)
    {
        if (route != target)
        {
            return false;
        }

        await FakePve.Reply(ctx, status, reason, null);
        return true;
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
