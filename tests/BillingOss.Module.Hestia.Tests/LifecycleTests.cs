using System.Net;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Hestia.Tests;

/// <summary>
/// suspend, unsuspend y terminate como transiciones verificadas: estado antes, mutador solo si hace falta y estado
/// después. Incluye casos de prueba del módulo anterior, portados a C#.
/// </summary>
[TestClass]
public sealed class LifecycleTests
{
    private static readonly string[] StateRead =
        ["v-list-user", "v-list-web-domains", "v-list-mail-domains", "v-list-dns-domains", "v-list-databases", "v-list-cron-jobs"];

    [TestMethod]
    public async Task YaSuspendido_NoMuta_YSoloLeeElEstado()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount(suspended: true));

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
        CollectionAssert.AreEqual(StateRead, host.Hestia.Commands);
    }

    [TestMethod]
    public async Task Suspender_MandaElMutador_YVerificaLosCincoRecursos()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
        Assert.AreEqual("alice", body!["result"]!["external_ids"]!["username"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "v-suspend-user" }, host.Hestia.Mutations);
        CollectionAssert.AreEqual(new[] { "alice" }, host.Hestia.Calls.Single(c => c.Command == "v-suspend-user").Args);
        Assert.IsTrue(host.Hestia.Users["alice"].Suspended);
    }

    [TestMethod]
    public async Task Reactivar_ConvergeAActivo()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount(suspended: true));

        var (status, body) = await host.Op("unsuspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("active", HestiaHost.Result(body, "observed_state"));
        CollectionAssert.AreEqual(new[] { "v-unsuspend-user" }, host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task RecursosParciales_FallanConConflict_SinMutar()
    {
        var hestia = new FakeHestia().WithAccount(suspended: true);
        hestia.Users["alice"].Resources["web"]["example.test"] = false;
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.Conflict, HestiaHost.Code(body));
        Assert.AreEqual(60, body!["error"]!["retry_after"]!.GetValue<int>());
        CollectionAssert.AreEqual(new[] { "web" }, HestiaHost.Strings(body["error"]!["details"]!["resources"]));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task UnCronActivo_ImpideDarPorSuspendido()
    {
        var hestia = new FakeHestia().WithAccount(suspended: true);
        hestia.Users["alice"].Resources["cron"]["1"] = false;
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(ErrorCodes.Conflict, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    [DataRow("""{"guest":{"SUSPENDED":"no"}}""", ErrorCodes.UpstreamUnavailable)]
    [DataRow("""{"alice":{"SUSPENDED":"tal vez"}}""", ErrorCodes.UpstreamUnavailable)]
    [DataRow("no-es-json", ErrorCodes.UpstreamUnavailable)]
    [DataRow("Error: authentication failed", ErrorCodes.InvalidConfig)]
    [DataRow("Error: algo raro", ErrorCodes.UpstreamUnavailable)]
    public async Task UsuarioAjenoJsonRotoOAutenticacion_FallanSinMutar(string answer, string code)
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount().On("v-list-user", 200, answer));

        foreach (var operation in new[] { "suspend", "unsuspend", "terminate" })
        {
            var (_, body) = await host.Op(operation, HestiaHost.Existing());
            Assert.AreEqual(code, HestiaHost.Code(body), operation);
        }

        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task CorteEnLaLectura_EsUpstreamUnavailable_SinMutar()
    {
        var hestia = new FakeHestia().WithAccount().CutAfter("v-list-user", apply: false);
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, HestiaHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task MutadorConTextoDeError_PeroEstadoQueConverge_Termina()
    {
        var hestia = new FakeHestia().WithAccount().On("v-suspend-user", (_, run) =>
        {
            run();
            return (400, "Error: user alice is suspended");
        });
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task AcusePerdido_ConEstadoQueConverge_Termina()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount().CutAfter("v-suspend-user", apply: true));

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task AcusePerdido_SinConverger_QuedaEnCurso_YLaConsultaSoloRelee_HastaQueHestiaTermina()
    {
        var hestia = new FakeHestia().WithAccount().CutAfter("v-suspend-user", apply: false);
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");

        // Hestia puede seguir ejecutando el comando: mandarlo de nuevo ahora duplicaría el efecto.
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("pending", body!["status"]!.GetValue<string>());
        var id = body["operation_id"]!.GetValue<string>();
        Assert.StartsWith("suspend-svc1042-sent-", id);

        // La misma clave devuelve la misma operación, y la consulta dentro de la ventana no manda otro mutador.
        var repeated = await host.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");
        Assert.AreEqual(body.ToJsonString(), repeated.Body!.ToJsonString());
        (_, body) = await host.Send($"/v0/operations/{id}", [], context: HestiaHost.Existing());
        Assert.AreEqual(id, body!["operation_id"]!.GetValue<string>());

        // Hestia termina el comando por su cuenta: la consulta siguiente lo ve y completa.
        hestia.Users["alice"].SetAll(true);
        (status, body) = await host.Send($"/v0/operations/{id}", [], context: HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
        Assert.AreEqual(1, hestia.Count("v-suspend-user"));
    }

    [TestMethod]
    public async Task AcusePerdido_PasadaLaVentana_LaConsultaMandaElMutadorDeNuevo()
    {
        var clock = new StepClock();
        var hestia = new FakeHestia().WithAccount().CutAfter("v-suspend-user", apply: false);
        await using var host = await HestiaHost.Start(hestia, time: clock);

        var (_, body) = await host.Op("suspend", HestiaHost.Existing());
        var id = body!["operation_id"]!.GetValue<string>();

        hestia.On("v-suspend-user", (_, run) => run());
        clock.Advance(TimeSpan.FromMinutes(11));
        var (status, polled) = await host.Send($"/v0/operations/{id}", [], context: HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status, polled?.ToJsonString());
        Assert.AreEqual("suspended", HestiaHost.Result(polled, "observed_state"));
        Assert.AreEqual(2, hestia.Count("v-suspend-user"));
    }

    [TestMethod]
    public async Task SinConexionJustoEnElMutador_EsUpstreamUnavailable_PorqueElPedidoNoSalio()
    {
        var hestia = new FakeHestia().WithAccount().On("v-suspend-user", (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "rechazada"));
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, body?.ToJsonString());
        Assert.AreEqual("no_connection", HestiaHost.Reason(body));
        Assert.IsFalse(hestia.Users["alice"].Suspended);
    }

    [TestMethod]
    public async Task MutadorRechazado_SinConverger_EsRejectedConElTextoDeHestia()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount().On("v-suspend-user", 400, "Error: disk quota exceeded"));

        var (_, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(ErrorCodes.Rejected, HestiaHost.Code(body));
        Assert.AreEqual("Error: disk quota exceeded", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(false, ErrorCodes.Rejected)]
    [DataRow(true, null)]
    public async Task UnTextoComoElDeAutenticacion_EnLaSalidaDeUnMutador_LoDecideLaRelectura(bool applied, string? code)
    {
        // La salida de un comando puede traer un "not allowed to connect" de la base: no es la API negando la access key.
        var hestia = new FakeHestia().WithAccount().On("v-suspend-user", (_, run) =>
        {
            if (applied)
            {
                run();
            }

            return (400, "Error: Host 'web1' is not allowed to connect to this MariaDB server");
        });
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(code, HestiaHost.Code(body), body?.ToJsonString());
        if (applied)
        {
            Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
        }
    }

    [TestMethod]
    public async Task UnCronQueSigueActivoDespuesDelMutador_NoSeDaPorSuspendido()
    {
        // Hestia contesta OK y deja un recurso sin suspender: la verificación posterior lo tiene que ver.
        var hestia = new FakeHestia().WithAccount();
        hestia.On("v-suspend-user", (_, run) =>
        {
            var answer = run();
            hestia.Users["alice"].Resources["cron"]["1"] = false;
            return answer;
        });
        await using var host = await HestiaHost.Start(hestia);

        var (_, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, HestiaHost.Code(body), body?.ToJsonString());
        Assert.AreEqual("not_converged", HestiaHost.Reason(body));
        Assert.IsNull(HestiaHost.Result(body, "observed_state"));
    }

    [TestMethod]
    [DataRow(500, "Error: disk quota exceeded")]
    [DataRow(502, "<html>Bad Gateway</html>")]
    [DataRow(200, "<html>portal</html>")]
    public async Task RespuestaQueNoEsUnRechazoDeHestia_SinConverger_QuedaEnCurso(int code, string text)
    {
        // Un 5xx o un cuerpo que no es de Hestia puede venir de algo intermedio con el comando todavía corriendo.
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount().On("v-suspend-user", code, text));

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.StartsWith("suspend-svc1042-sent-", body!["operation_id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task MismaClaveDosVeces_UnSoloMutador_YLaMismaRespuesta()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var first = await host.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");
        var second = await host.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");

        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
        Assert.AreEqual(1, host.Hestia.Count("v-suspend-user"));
        Assert.AreEqual(2, host.Hestia.Count("v-list-user"), "Una lectura antes y otra después, solo en la primera llamada.");

        // La misma clave con otra entrada es un conflicto de idempotencia.
        var (status, body) = await host.Op("suspend", HestiaHost.Existing(), input: new() { ["otra"] = 1 }, key: "svc-1042-suspend-1");
        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, HestiaHost.Code(body));
    }

    [TestMethod]
    public async Task OtraReplica_ConLaMismaClave_NoRepiteElEfecto()
    {
        // Dos réplicas con registros de idempotencia separados: la segunda lee el estado y ve que ya está hecho.
        var hestia = new FakeHestia().WithAccount();
        await using var first = await HestiaHost.Start(hestia);
        await using var second = await HestiaHost.Start(hestia);

        var a = await first.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");
        var b = await second.Op("suspend", HestiaHost.Existing(), key: "svc-1042-suspend-1");

        Assert.AreEqual(HttpStatusCode.OK, a.Status);
        Assert.AreEqual(HttpStatusCode.OK, b.Status);
        Assert.AreEqual(1, hestia.Count("v-suspend-user"));
    }

    [TestMethod]
    public async Task UsuarioAusente_NuncaSeSuspendeNiReactiva_EsNotFound()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        foreach (var operation in new[] { "suspend", "unsuspend", "change_plan" })
        {
            var (status, body) = await host.Op(operation, HestiaHost.Existing());
            Assert.AreEqual(HttpStatusCode.NotFound, status, operation);
            Assert.AreEqual(ErrorCodes.NotFound, HestiaHost.Code(body), operation);
        }

        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task Baja_DeUnUsuarioAusente_TerminaSinMutar_ConElInventarioAutenticado()
    {
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (status, body) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("terminated", HestiaHost.Result(body, "observed_state"));
        CollectionAssert.AreEqual(new[] { "v-list-user", "v-list-users" }, host.Hestia.Commands);
    }

    [TestMethod]
    public async Task Baja_BorraYPruebaLaAusencia()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var (status, body) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("terminated", HestiaHost.Result(body, "observed_state"));
        CollectionAssert.AreEqual(new[] { "v-delete-user" }, host.Hestia.Mutations);
        Assert.AreEqual("v-list-users", host.Hestia.Commands[^1]);
        Assert.IsFalse(host.Hestia.Users.ContainsKey("alice"));
    }

    [TestMethod]
    public async Task Baja_ConRecursosParciales_SigueAdelante()
    {
        var hestia = new FakeHestia().WithAccount();
        hestia.Users["alice"].Resources["mail"]["example.test"] = true;
        await using var host = await HestiaHost.Start(hestia);

        var (status, _) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, hestia.Count("v-delete-user"));
    }

    [TestMethod]
    [DataRow("Error: user alice does not exist")]
    [DataRow("Error: user alicia doesn't exist")]
    public async Task Baja_ConUnTextoDeInexistenciaQueNoEsElExacto_NoPruebaLaAusencia(string text)
    {
        await using var host = await HestiaHost.Start(new FakeHestia().On("v-list-user", 400, text));

        var (_, body) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, HestiaHost.Code(body));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("""{"alice":{"SUSPENDED":"no"}}""")]
    [DataRow("""{"error":"access denied"}""")]
    [DataRow("Error: algo raro")]
    public async Task Baja_SinUnInventarioQuePruebeLaAusencia_FallaCerrada(string? inventory)
    {
        var hestia = new FakeHestia();
        if (inventory is null)
        {
            hestia.CutAfter("v-list-users", apply: false);
        }
        else
        {
            hestia.On("v-list-users", 200, inventory);
        }

        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreNotEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, HestiaHost.Code(body));
        Assert.IsEmpty(hestia.Mutations);
    }

    [TestMethod]
    public async Task Baja_UnRecursoQueRespondeError_FallaSinBorrar()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount().On("v-list-mail-domains", 400, "Error: user alice doesn't exist"));

        var (_, body) = await host.Op("terminate", HestiaHost.Existing());

        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, HestiaHost.Code(body));
        Assert.AreEqual("mail_refused", HestiaHost.Reason(body));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    [DataRow("other.test")]
    [DataRow("exampleXtest")]
    [DataRow("sub.example.test")]
    public async Task UnDominioQueNoEsExactamenteElDelServicio_NoCuentaComoPropio(string domain)
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount(domain: domain));

        var (status, body) = await host.Op("terminate", HestiaHost.Existing(domain: "example.test"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.InvalidInput, HestiaHost.Code(body));
        CollectionAssert.AreEqual(new[] { "external_ids.domain" }, HestiaHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(host.Hestia.Mutations);
    }

    [TestMethod]
    public async Task UnDominioSoloEnDns_EsPropio()
    {
        // Los paquetes dns-managment y DNSZONE pueden no tener web.
        var hestia = new FakeHestia().WithAccount(package: "dns-managment");
        hestia.Users["alice"].Resources["web"].Clear();
        hestia.Users["alice"].Resources["mail"].Clear();
        await using var host = await HestiaHost.Start(hestia);

        var (status, body) = await host.Op("suspend", HestiaHost.Existing());

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("suspended", HestiaHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task ElDominioSeComparaEnMinusculas()
    {
        await using var host = await HestiaHost.Start(new FakeHestia().WithAccount());

        var (status, body) = await host.Op("suspend", HestiaHost.Existing(domain: " Example.TEST. "));

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("example.test", body!["result"]!["external_ids"]!["domain"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnCuerpoConErrorYHttp400_SeLeeComoErrorDeHestia()
    {
        // La Hestia falsa contesta la inexistencia con HTTP 400: el texto se conserva y prueba la ausencia con el inventario.
        await using var host = await HestiaHost.Start(new FakeHestia());

        var (_, body) = await host.Op("get_status", HestiaHost.Existing());

        Assert.AreEqual("not_found", HestiaHost.Result(body, "observed_state"));
    }
}
