using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Protocol;

namespace BillingOss.Modules.Hosting.Tests;

/// <summary>create idempotente por estado, la marca con el uuid del servicio, la consulta previa y la continuación por consulta.</summary>
[TestClass]
public sealed class CreateTests
{
    private static readonly string Username = Usernames.For("c", ModuleHost.Uuid);

    [TestMethod]
    public async Task Alta_UsuarioDelUuid_ConPlanCorreoNombreLimpioYMarca()
    {
        var panel = new FakePanel { KeepsMarker = true };
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual(Username, ModuleHost.Username(body));
        Assert.AreEqual("nuevo.example.test", body!["result"]!["external_ids"]!["domain"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { $"create:{Username}" }, panel.Mutations);

        var account = panel.Accounts[Username];
        Assert.AreEqual("basico", account.Plan);
        Assert.AreEqual(ModuleHost.Password, account.Password);
        Assert.AreEqual("cliente@example.com", account.Email);
        Assert.AreEqual("Cliente de Prueba S.A.", account.Name);
        Assert.AreEqual(ModuleHost.Uuid, account.Marker);

        // Antes de crear: la cuenta por su usuario y el dueño del dominio. Después, la relectura.
        CollectionAssert.AreEqual(
            new[] { $"read:{Username}", "owner:nuevo.example.test", $"create:{Username}", $"read:{Username}" },
            panel.Calls.ToArray());
    }

    [TestMethod]
    public void ElUsuario_SaleDelUuid_SiempreIgual_YSinChocarConOtroServicio()
    {
        var first = Usernames.For("c", ModuleHost.Uuid);

        // El cálculo no puede cambiar nunca: es la marca de las cuentas que ya existen en los paneles.
        Assert.AreEqual("cp4txcpw", first);
        Assert.AreEqual(first, Usernames.For("c", ModuleHost.Uuid.ToUpperInvariant()));
        Assert.AreEqual(Usernames.Length, first.Length);
        Assert.StartsWith("c", first);
        Assert.IsTrue(first.All(c => char.IsAsciiLetterLower(c) || c is >= '2' and <= '7'), first);
        Assert.AreNotEqual(first, Usernames.For("c", "0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e12"));
        Assert.AreEqual(8, Usernames.For("host", ModuleHost.Uuid).Length);
        Assert.StartsWith("host", Usernames.For("host", ModuleHost.Uuid));
    }

    [TestMethod]
    public async Task MismaClave_DosVeces_UnSoloEfecto_YLaMismaRespuesta()
    {
        var panel = new FakePanel();
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var first = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials(), key: "svc-1042-create-1");
        var second = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials(), key: "svc-1042-create-1");
        var other = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials("Otra-Contrasena_123"), key: "svc-1042-create-1");

        Assert.AreEqual(first.Body!.ToJsonString(), second.Body!.ToJsonString());
        Assert.HasCount(1, panel.Mutations);
        Assert.AreEqual(HttpStatusCode.Conflict, other.Status);
        Assert.AreEqual(ErrorCodes.IdempotencyConflict, ModuleHost.Code(other.Body));
    }

    [TestMethod]
    public async Task OtraReplica_SinElRegistro_EncuentraLaCuenta_YNoLaVuelveACrear()
    {
        var panel = new FakePanel();
        await using var first = await ModuleHost.Start(FakePanel.Module(panel), []);
        await using var second = await ModuleHost.Start(FakePanel.Module(panel), []);

        await first.Op("create", ModuleHost.NewService(), ModuleHost.Credentials(), key: "svc-1042-create-1");
        var (status, body) = await second.Op("create", ModuleHost.NewService(), ModuleHost.Credentials(), key: "svc-1042-create-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(Username, ModuleHost.Username(body));
        CollectionAssert.AreEqual(new[] { $"create:{Username}" }, panel.Mutations);
    }

    [TestMethod]
    [DataRow("11111111-2222-4333-8444-555555555555")]
    [DataRow("cliente-1234")]
    [DataRow(null)]
    public async Task ConsultaPrevia_ElDominioEnOtraCuenta_FrenaElAlta_SinCrearNada(string? marker)
    {
        // Solo la marca de este servicio permite adoptar la cuenta: ni la de otro, ni el ID de otro sistema, ni ninguna.
        var panel = new FakePanel { KeepsMarker = true }.With("otro", "nuevo.example.test", marker: marker);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(ErrorCodes.Rejected, ModuleHost.Code(body));
        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task ConsultaPrevia_ElDominioEnUnaCuentaConLaMarcaDelServicio_LaAdopta()
    {
        // Un alta anterior con otro prefijo: la marca dice que es de este servicio, así que no se crea otra.
        var panel = new FakePanel { KeepsMarker = true }.With("viejo1", "nuevo.example.test", marker: ModuleHost.Uuid);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("viejo1", ModuleHost.Username(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task ConsultaPrevia_SinMarcaEnElPanel_UnDominioAjenoFrena_AunqueSeaDelMismoCliente()
    {
        var panel = new FakePanel().With("viejo1", "nuevo.example.test", marker: ModuleHost.Uuid);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual("domain_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    [DataRow(false, "otro.example.test", null, DisplayName = "Otro dominio")]
    [DataRow(true, "nuevo.example.test", "11111111-2222-4333-8444-555555555555", DisplayName = "Marca de otro servicio")]
    [DataRow(true, "", "11111111-2222-4333-8444-555555555555", DisplayName = "Incompleta con marca ajena")]
    public async Task ElUsuarioTomadoPorOtraCuenta_EsRejected_SinTocarla(bool keepsMarker, string domain, string? marker)
    {
        var panel = new FakePanel { KeepsMarker = keepsMarker }.With(Username, domain, marker: marker);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("user_taken", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task CuentaIncompletaConLaMarca_SeCompleta()
    {
        // El cliente quedó creado y la suscripción no, como un alta de Plesk cortada a la mitad.
        var panel = new FakePanel { KeepsMarker = true }.With(Username, string.Empty, marker: ModuleHost.Uuid);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("nuevo.example.test", panel.Accounts[Username].Domain);
        CollectionAssert.AreEqual(new[] { $"create:{Username}" }, panel.Mutations);
    }

    [TestMethod]
    public async Task CuentaDeUnAltaAnteriorConOtroPlan_SeCambiaDePlan()
    {
        var panel = new FakePanel().With(Username, "nuevo.example.test", plan: "pro");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("basico", panel.Accounts[Username].Plan);
        CollectionAssert.AreEqual(new[] { $"change_plan:{Username}" }, panel.Mutations);
    }

    [TestMethod]
    public async Task CuentaDelServicioSuspendida_EsManualReview()
    {
        var panel = new FakePanel().With(Username, "nuevo.example.test", suspended: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.Conflict, status);
        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body));
        Assert.AreEqual("suspended", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task CorteDespuesDeCrear_LaRelecturaConfirma()
    {
        var panel = new FakePanel().Lost("create", apply: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
    }

    [TestMethod]
    public async Task CorteSinQueElPanelCree_EsOutcomeUnknown_YElReintentoCrea()
    {
        var panel = new FakePanel().Lost("create", apply: false);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials(), key: "k1");

        // Antes de que la cuenta exista el alta no puede quedar en curso: la consulta no trae la contraseña.
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, ModuleHost.Code(body));
        Assert.AreEqual("no_reply", ModuleHost.Reason(body));

        panel.On("create", run => run());
        (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials(), key: "k1");
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.HasCount(2, panel.Mutations);
    }

    [TestMethod]
    public async Task ElPanelRechazaElAlta_EsRejectedConSuTexto()
    {
        var panel = new FakePanel().Refuse("create", "El paquete no permite más cuentas");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("upstream_rejected", ModuleHost.Reason(body));
        Assert.AreEqual("El paquete no permite más cuentas", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task OkSinQueLaCuentaAparezca_EsOutcomeUnknown()
    {
        var panel = new FakePanel().On("create", _ => Ack.Done);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(ErrorCodes.OutcomeUnknown, ModuleHost.Code(body));
        Assert.AreEqual("not_converged", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task UnaLecturaQueFallaDespuesDeCrear_EsOutcomeUnknown()
    {
        var panel = new FakePanel();
        panel.On("create", run =>
        {
            panel.FailingReads = 1;
            return run();
        });
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual("unreadable_after_change", ModuleHost.Reason(body));
    }

    [TestMethod]
    [DataRow("credentials", DisplayName = "Sin contraseña")]
    [DataRow("email", DisplayName = "Sin correo")]
    [DataRow("uuid", DisplayName = "Sin uuid")]
    [DataRow("domain", DisplayName = "Dominio inválido")]
    [DataRow("plan", DisplayName = "Plan inválido")]
    public async Task DatosQueFaltan_NoSalenAlPanel(string missing)
    {
        var panel = new FakePanel();
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);
        var context = ModuleHost.NewService();
        var input = ModuleHost.Credentials();
        string expected;
        switch (missing)
        {
            case "credentials":
                input = [];
                expected = "credentials.password";
                break;
            case "email":
                context["account"]!["email"] = "no-es-un-correo";
                expected = "account.email";
                break;
            case "uuid":
                context["service"]!["uuid"] = "1042";
                expected = "service.uuid";
                break;
            case "domain":
                context["service"]!["order"]!["domain"] = "sin_punto";
                expected = "domain";
                break;
            default:
                context["service"]!["params"]!["plan"] = " con espacios ";
                expected = "plan";
                break;
        }

        var (status, body) = await host.Op("create", context, input);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual(missing == "plan" ? ErrorCodes.InvalidConfig : ErrorCodes.InvalidInput, ModuleHost.Code(body));
        CollectionAssert.Contains(ModuleHost.Strings(body!["error"]!["details"]!["fields"]), expected);
        Assert.IsEmpty(panel.Calls);
    }

    [TestMethod]
    public async Task ElDominioSeNormaliza_APunycodeYMinusculas()
    {
        var panel = new FakePanel();
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("create", ModuleHost.NewService("Peñarol.Example.TEST."), ModuleHost.Credentials());

        Assert.AreEqual("xn--pearol-xwa.example.test", body!["result"]!["external_ids"]!["domain"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinTiempoAntesDeCrear_EsUpstreamUnavailable_SinCambios()
    {
        var clock = new StepClock();
        var panel = new FakePanel { Clock = clock, Step = TimeSpan.FromSeconds(50) };
        await using var host = await ModuleHost.Start(FakePanel.Module(panel, clock), []);

        var (status, body) = await host.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status, body?.ToJsonString());
        Assert.AreEqual("out_of_time", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task SinTiempoConLaCuentaCreada_QuedaEnCurso_YLaConsultaDeOtraReplicaTermina()
    {
        // La cuenta existe con otro plan y no queda tiempo para cambiarlo: la consulta lo termina sin la contraseña.
        var clock = new StepClock();
        var panel = new FakePanel { Clock = clock, Step = TimeSpan.FromSeconds(100) }.With(Username, "nuevo.example.test", plan: "pro");
        await using var first = await ModuleHost.Start(FakePanel.Module(panel, clock), []);
        await using var second = await ModuleHost.Start(FakePanel.Module(panel, clock), []);

        var (status, body) = await first.Op("create", ModuleHost.NewService(), ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(ModuleResponse.PendingStatus, body!["status"]!.GetValue<string>());
        var id = body["operation_id"]!.GetValue<string>();
        Assert.AreEqual($"create-{ModuleHost.Uuid}", id);
        Assert.IsEmpty(panel.Mutations);

        panel.Step = TimeSpan.Zero;
        (status, body) = await second.Poll(id, ModuleHost.NewService());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("active", ModuleHost.Result(body, "observed_state"));
        CollectionAssert.AreEqual(new[] { $"change_plan:{Username}" }, panel.Mutations);
    }

    [TestMethod]
    public async Task LaConsultaDeUnAltaSinCuenta_NoCreaNada_PorqueNoTraeLaContrasena()
    {
        var panel = new FakePanel();
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Poll($"create-{ModuleHost.Uuid}", ModuleHost.NewService());

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual("account_incomplete", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task UnServicioImportado_ConUsuarioEnExternalIds_UsaEseUsuario()
    {
        var panel = new FakePanel().With("importado", "example.test");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);
        var context = ModuleHost.NewService("example.test");
        context["service"]!["external_ids"] = new JsonObject { ["username"] = "importado" };

        var (status, body) = await host.Op("create", context, ModuleHost.Credentials());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("importado", ModuleHost.Username(body));
        Assert.IsEmpty(panel.Mutations);
    }
}
