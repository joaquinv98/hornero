using System.Globalization;
using System.Net;
using BillingOss.Modules.Hosting.Testing;
using BillingOss.Modules.Protocol;

namespace BillingOss.Modules.Hosting.Tests;

/// <summary>suspend, unsuspend, terminate, change_plan y change_password: leer, cambiar solo lo que falta y releer.</summary>
[TestClass]
public sealed class LifecycleTests
{
    private const string Foreign = "11111111-2222-4333-8444-555555555555";

    [TestMethod]
    public async Task SuspensionYReactivacion_SoloMutanCuandoHaceFalta()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);
        var context = ModuleHost.Existing("alice");

        foreach (var (operation, state) in new[] { ("suspend", "suspended"), ("suspend", "suspended"), ("unsuspend", "active"), ("unsuspend", "active") })
        {
            var (status, body) = await host.Op(operation, context);
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            Assert.AreEqual(state, ModuleHost.Result(body, "observed_state"));
            Assert.AreEqual("alice", ModuleHost.Username(body));
        }

        CollectionAssert.AreEqual(new[] { "suspend:alice", "unsuspend:alice" }, panel.Mutations);
    }

    [TestMethod]
    [DataRow("suspend")]
    [DataRow("unsuspend")]
    [DataRow("change_plan")]
    [DataRow("run_action")]
    [DataRow("sso")]
    [DataRow("usage")]
    public async Task CuentaAusente_EsNotFound(string operation)
    {
        var panel = new FakePanel();
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op(operation, ModuleHost.Existing("alice"), ModuleHost.AnyInput());

        Assert.AreEqual(HttpStatusCode.NotFound, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.NotFound, ModuleHost.Code(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    [DataRow("suspend")]
    [DataRow("unsuspend")]
    [DataRow("terminate")]
    [DataRow("change_plan")]
    [DataRow("run_action")]
    [DataRow("sso")]
    [DataRow("usage")]
    public async Task CuentaConOtroDominio_EsManualReview_SinTocarla(string operation)
    {
        var panel = new FakePanel().With("alice", "ajeno.example.test", plan: "pro");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op(operation, ModuleHost.Existing("alice"), ModuleHost.AnyInput());

        Assert.AreEqual(HttpStatusCode.Conflict, status, body?.ToJsonString());
        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body));
        Assert.AreEqual("domain_mismatch", ModuleHost.Reason(body));
        Assert.IsFalse(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsEmpty(panel.Mutations);
        Assert.IsTrue(panel.Accounts.ContainsKey("alice"));
    }

    [TestMethod]
    [DataRow("suspend")]
    [DataRow("terminate")]
    [DataRow("change_plan")]
    public async Task CuentaConLaMarcaDeOtroServicio_EsManualReview_SinTocarla(string operation)
    {
        var panel = new FakePanel { KeepsMarker = true }.With("alice", marker: Foreign, plan: "pro");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op(operation, ModuleHost.Existing("alice"));

        Assert.AreEqual(ErrorCodes.ManualReview, ModuleHost.Code(body));
        Assert.AreEqual("marker_mismatch", ModuleHost.Reason(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("cliente-1234")]
    [DataRow("1234")]
    public async Task CuentaImportadaSinMarcaOConElIdDeOtroSistema_SeOperaPorElDominio(string? marker)
    {
        // Un valor que no es un uuid lo dejó otro sistema, por ejemplo el de facturación anterior: no dice de qué servicio es.
        var panel = new FakePanel { KeepsMarker = true }.With("alice", marker: marker);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("suspend", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.IsTrue(panel.Accounts["alice"].Suspended);

        (_, body) = await host.Op("get_status", ModuleHost.Existing("alice"));
        Assert.AreEqual("suspended", ModuleHost.Result(body, "observed_state"));
        Assert.IsNull(body!["result"]!["details"]!["reason"]);
    }

    [TestMethod]
    public async Task SuspensionQueNoConverge_EsOutcomeUnknown_YRechazada_EsRejected()
    {
        var panel = new FakePanel().With("alice").On("suspend", _ => Ack.Done);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("suspend", ModuleHost.Existing("alice"));
        Assert.AreEqual(ErrorCodes.OutcomeUnknown, ModuleHost.Code(body));
        Assert.AreEqual("not_converged", ModuleHost.Reason(body));

        panel.Refuse("suspend", "La cuenta está bloqueada por otra tarea");
        (_, body) = await host.Op("suspend", ModuleHost.Existing("alice"));
        Assert.AreEqual(ErrorCodes.Rejected, ModuleHost.Code(body));
        Assert.AreEqual("La cuenta está bloqueada por otra tarea", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UnaLecturaQueFalla_AntesDeCambiar_EsUpstreamUnavailable_SinMutar()
    {
        var panel = new FakePanel { FailingReads = 1 }.With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, ModuleHost.Code(body));
        Assert.IsEmpty(panel.Mutations);
    }

    [TestMethod]
    public async Task Baja_BorraYPruebaLaAusencia_YRepetirlaNoMuta()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));

        (status, body) = await host.Op("terminate", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));

        CollectionAssert.AreEqual(new[] { "delete:alice" }, panel.Mutations);
        Assert.IsEmpty(panel.Accounts);
    }

    [TestMethod]
    public async Task BajaDeUnAltaAMedias_ConLaMarcaDelServicio_LaBorra_PeroNoSeSuspende()
    {
        // El cliente de Plesk quedó creado con la marca y sin la suscripción del dominio.
        var panel = new FakePanel { KeepsMarker = true }.With("alice", string.Empty, marker: ModuleHost.Uuid);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (_, body) = await host.Op("suspend", ModuleHost.Existing("alice"));
        Assert.AreEqual("domain_mismatch", ModuleHost.Reason(body));

        var (status, terminated) = await host.Op("terminate", ModuleHost.Existing("alice"));
        Assert.AreEqual(HttpStatusCode.OK, status, terminated?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(terminated, "observed_state"));
        CollectionAssert.AreEqual(new[] { "delete:alice" }, panel.Mutations);
    }

    [TestMethod]
    public async Task BajaSinRespuesta_QuedaEnCurso_YLaConsultaSoloReleeDuranteLaVentana()
    {
        // La baja de una cuenta grande sigue corriendo en el panel después del corte: mandarla otra vez la duplicaría.
        var clock = new StepClock();
        var panel = new FakePanel { Clock = clock }.With("alice").Lost("delete", apply: false);
        await using var first = await ModuleHost.Start(FakePanel.Module(panel, clock), []);
        await using var second = await ModuleHost.Start(FakePanel.Module(panel, clock), []);
        var context = ModuleHost.Existing("alice");

        var (status, body) = await first.Op("terminate", context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var id = body!["operation_id"]!.GetValue<string>();
        var sent = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Assert.AreEqual($"terminate-{ModuleHost.Uuid}-sent-{sent}", id);

        // Dentro de la ventana la consulta no manda otro borrado, aunque la cuenta siga.
        (status, body) = await second.Poll(id, context);
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(id, body!["operation_id"]!.GetValue<string>());
        Assert.HasCount(1, panel.Mutations);

        // El panel termina el borrado: la consulta lo confirma.
        panel.Accounts.Remove("alice");
        (status, body) = await second.Poll(id, context);
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(body, "observed_state"));
        Assert.HasCount(1, panel.Mutations);
    }

    [TestMethod]
    public async Task PasadaLaVentana_LaConsultaCorreLaOperacionEntera()
    {
        var clock = new StepClock();
        var panel = new FakePanel { Clock = clock }.With("alice").Lost("delete", apply: false);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel, clock), []);
        var context = ModuleHost.Existing("alice");

        var (_, body) = await host.Op("terminate", context);
        var id = body!["operation_id"]!.GetValue<string>();

        clock.Advance(TimeSpan.FromMinutes(11));
        panel.On("delete", run => run());
        var (status, done) = await host.Poll(id, context);

        Assert.AreEqual(HttpStatusCode.OK, status, done?.ToJsonString());
        Assert.AreEqual("terminated", ModuleHost.Result(done, "observed_state"));
        CollectionAssert.AreEqual(new[] { "delete:alice", "delete:alice" }, panel.Mutations);
    }

    [TestMethod]
    public async Task SinTiempoParaSuspender_QuedaEnCurso_YLaConsultaTermina()
    {
        var clock = new StepClock();
        var panel = new FakePanel { Clock = clock, Step = TimeSpan.FromSeconds(70) }.With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel, clock), []);
        var context = ModuleHost.Existing("alice");

        var (_, body) = await host.Op("suspend", context);

        Assert.AreEqual($"suspend-{ModuleHost.Uuid}", body!["operation_id"]!.GetValue<string>());
        Assert.IsEmpty(panel.Mutations);

        panel.Step = TimeSpan.Zero;
        var (status, done) = await host.Poll(body["operation_id"]!.GetValue<string>(), context);
        Assert.AreEqual(HttpStatusCode.OK, status, done?.ToJsonString());
        Assert.AreEqual("suspended", ModuleHost.Result(done, "observed_state"));
    }

    [TestMethod]
    [DataRow("otra-cosa")]
    [DataRow("suspend-11111111-2222-4333-8444-555555555555")]
    [DataRow("suspend-0b6c4a52-6a4e-4c2e-9f7d-3d2f1c0a9e11-sent-abc")]
    public async Task UnaConsultaDeOtraOperacionOServicio_EsNotFound(string id)
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Poll(id, ModuleHost.Existing("alice"));

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, ModuleHost.Code(body));
        Assert.IsEmpty(panel.Calls);
    }

    [TestMethod]
    public async Task CambioDePlan_CambiaYVerifica_YConElMismoPlanNoMuta()
    {
        var panel = new FakePanel().With("alice", plan: "basico", suspended: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("change_plan", ModuleHost.Existing("alice", plan: "Plan Empresa"));
        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("suspended", ModuleHost.Result(body, "observed_state"));
        Assert.AreEqual("Plan Empresa", panel.Accounts["alice"].Plan);

        (status, _) = await host.Op("change_plan", ModuleHost.Existing("alice", plan: "Plan Empresa"));
        Assert.AreEqual(HttpStatusCode.OK, status);
        CollectionAssert.AreEqual(new[] { "change_plan:alice" }, panel.Mutations);
    }

    [TestMethod]
    public async Task CambioDePlanRechazado_EsRejectedConElTextoDelPanel()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("change_plan", ModuleHost.Existing("alice", plan: "inexistente"));

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("No existe el plan inexistente", body!["error"]!["details"]!["upstream"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task CambioDeContrasena_UsaLaQueMandaElNucleo()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("run_action", ModuleHost.Existing("alice"), ModuleHost.ChangePassword("Nueva-Contrasena_99"));

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual("change_password", ModuleHost.Result(body, "action"));
        Assert.AreEqual("Nueva-Contrasena_99", panel.Accounts["alice"].Password);
    }

    [TestMethod]
    public async Task CambioDeContrasenaSinRespuesta_EsOutcomeUnknown_YRechazado_EsRejected()
    {
        var panel = new FakePanel().With("alice").Lost("change_password", apply: true);
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("run_action", ModuleHost.Existing("alice"), ModuleHost.ChangePassword());
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, status);
        Assert.AreEqual("no_reply", ModuleHost.Reason(body));

        panel.Refuse("change_password", "La contraseña es muy débil");
        (status, body) = await host.Op("run_action", ModuleHost.Existing("alice"), ModuleHost.ChangePassword());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        Assert.AreEqual("password_rejected", ModuleHost.Reason(body));
    }

    [TestMethod]
    public async Task OtraAccion_EsNotSupported_YSinContrasena_EsInvalidInput()
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);

        var (status, body) = await host.Op("run_action", ModuleHost.Existing("alice"), new() { ["action"] = "reboot" });
        Assert.AreEqual(HttpStatusCode.NotImplemented, status);
        Assert.AreEqual(ErrorCodes.NotSupported, ModuleHost.Code(body));

        (_, body) = await host.Op("run_action", ModuleHost.Existing("alice"), new() { ["action"] = "change_password" });
        Assert.AreEqual(ErrorCodes.InvalidInput, ModuleHost.Code(body));
        CollectionAssert.AreEqual(new[] { "credentials.password" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(panel.Calls);
    }

    [TestMethod]
    [DataRow("suspend")]
    [DataRow("terminate")]
    [DataRow("get_status")]
    [DataRow("view")]
    public async Task SinUsuarioODominioEnExternalIds_EsInvalidInput_SinSalirAlPanel(string operation)
    {
        var panel = new FakePanel().With("alice");
        await using var host = await ModuleHost.Start(FakePanel.Module(panel), []);
        var context = ModuleHost.Existing("alice");
        context["service"]!["external_ids"] = new System.Text.Json.Nodes.JsonObject { ["username"] = "con espacio" };

        var (status, body) = await host.Op(operation, context);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEquivalent(new[] { "external_ids.username", "external_ids.domain" }, ModuleHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(panel.Calls);
    }
}
