using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Suspensión, reactivación y baja (PTR-08 y PTR-09), y la acción de contraseña (PTR-13).</summary>
[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    public async Task SuspenderDosVeces_DaSuspendedLasDos_YEscribeUnaSola()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);

        var first = await host.Call("suspend", Samples.Context(), "svc-1042-suspend-1");
        var second = await host.Call("suspend", Samples.Context(), "svc-1042-suspend-2");

        foreach (var (status, body) in new[] { first, second })
        {
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            Assert.AreEqual("suspended", body!["result"]!["observed_state"]!.GetValue<string>());
            Assert.AreEqual(server.Id.ToString(), body["result"]!["external_ids"]!["server_id"]!.GetValue<string>());
        }

        Assert.AreEqual("suspended", server.Status);
        Assert.AreEqual(1, panel.Count($"POST servers/{server.Id}/suspend"));
    }

    [TestMethod]
    public async Task ReactivarDosVeces_DaActiveLasDos()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper, status: "suspended");
        await using var host = await PteroHost.Start(panel);

        var first = await host.Call("unsuspend", Samples.Context(), "svc-1042-unsuspend-1");
        var second = await host.Call("unsuspend", Samples.Context(), "svc-1042-unsuspend-2");

        Assert.AreEqual("active", first.Body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.AreEqual("active", second.Body!["result"]!["observed_state"]!.GetValue<string>());
        Assert.IsNull(server.Status);
        Assert.AreEqual(1, panel.Count($"POST servers/{server.Id}/unsuspend"));
    }

    [TestMethod]
    public async Task SuspenderUnServerQueNoExiste_EsNotFound()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        var (status, body) = await host.Call("suspend", Samples.Context(), "svc-1042-suspend-1");

        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(ErrorCodes.NotFound, PteroHost.Code(body));
    }

    [TestMethod]
    public async Task ErrorDelPanel_EsReintentable_YNoCambiaNada()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        panel.On($"POST servers/{server.Id}/suspend", HttpStatusCode.InternalServerError, "{}");
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("suspend", Samples.Context(), "svc-1042-suspend-1");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(body));
        Assert.IsTrue(body!["error"]!["retryable"]!.GetValue<bool>());
        Assert.IsNull(server.Status);
    }

    [TestMethod]
    public async Task ErrorAlBuscar_NuncaEsAusencia()
    {
        var panel = FakePanel.Standard();
        panel.On("GET servers/external/1042", HttpStatusCode.BadGateway, "<html>502</html>");
        await using var host = await PteroHost.Start(panel);

        var terminate = await host.Call("terminate", Samples.Context(), "svc-1042-terminate-1");
        var status = await host.Call("get_status", Samples.Context());

        // Una baja no da por borrado un server que no pudo leer, y el estado no dice not_found.
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(terminate.Body));
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(status.Body));
    }

    [TestMethod]
    public async Task Baja_BorraElServer_YRepetirlaDaTerminated()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);

        var first = await host.Call("terminate", Samples.Context(), "svc-1042-terminate-1");
        var second = await host.Call("terminate", Samples.Context(), "svc-1042-terminate-2");

        Assert.IsEmpty(panel.Servers);
        Assert.AreEqual(1, panel.Count($"DELETE servers/{server.Id}"));
        foreach (var (status, body) in new[] { first, second })
        {
            Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
            Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task Baja_ConElServerBorradoEntreLaLecturaYElDelete_DaTerminated()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        panel.On($"DELETE servers/{server.Id}", (_, _) => Samples.Error(HttpStatusCode.NotFound, "NotFoundHttpException", "The requested resource could not be found on the server."));
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("terminate", Samples.Context(), "svc-1042-terminate-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual("terminated", body!["result"]!["observed_state"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Baja_ConElNodoCaido_EsUpstreamUnavailable_YElServerSigue()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper);
        // Lo que contesta el panel: DaemonConnectionException con 504, y no borra nada.
        panel.On($"DELETE servers/{server.Id}", HttpStatusCode.GatewayTimeout, Samples.Error(HttpStatusCode.GatewayTimeout, "DaemonConnectionException", "Could not establish a connection to the machine running this server. Please try again.").Content.ReadAsStringAsync().Result);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("terminate", Samples.Context(), "svc-1042-terminate-1");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, PteroHost.Code(body));
        Assert.HasCount(1, panel.Servers);

        // Nunca el borrado forzado: eso lo decide el staff.
        Assert.AreEqual(0, panel.Count($"DELETE servers/{server.Id}/force"));
    }

    [TestMethod]
    public async Task CambiarLaContrasenaDelPanel_MandaLosDatosDelUsuarioYLaNueva()
    {
        var panel = FakePanel.Standard();
        var user = panel.AddUser("77", "cliente77@example.com");
        panel.AddServer(Samples.ServiceId, Samples.Paper, user: user.Id);
        await using var host = await PteroHost.Start(panel);
        var input = new JsonObject
        {
            ["action"] = "set_panel_password",
            ["credentials"] = new JsonObject { ["panel_password"] = "generada-por-el-nucleo-1" },
        };

        var (status, body) = await host.Call("run_action", Samples.Context(), "svc-1042-action-set_panel_password-1", input);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(user.Id.ToString(), body!["result"]!["user_id"]!.GetValue<string>());
        Assert.AreEqual("generada-por-el-nucleo-1", user.Password);
        Assert.AreEqual("cliente77@example.com", user.Email);
        Assert.DoesNotContain("generada-por-el-nucleo-1", body.ToJsonString());
    }

    [TestMethod]
    [DataRow(true, null)]
    [DataRow(false, "55")]
    public async Task CambiarLaContrasena_DeUnAdministradorODeOtraCuenta_NoSeHace(bool admin, string? account)
    {
        // El staff le asignó el server a un administrador para revisarlo, o el dueño es un usuario de otra cuenta.
        var panel = FakePanel.Standard();
        var owner = panel.AddUser(account, "dueno@example.com");
        owner.RootAdmin = admin;
        panel.AddServer(Samples.ServiceId, Samples.Paper, user: owner.Id);
        await using var host = await PteroHost.Start(panel);
        var input = new JsonObject
        {
            ["action"] = "set_panel_password",
            ["credentials"] = new JsonObject { ["panel_password"] = "generada-por-el-nucleo-1" },
        };

        var (status, body) = await host.Call("run_action", Samples.Context(), "svc-1042-action-set_panel_password-1", input);

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status, body?.ToJsonString());
        Assert.AreEqual("foreign_user", body!["error"]!["details"]!["upstream"]!["reason"]!.GetValue<string>());
        Assert.IsNull(owner.Password);
        Assert.AreEqual(0, panel.Writes);
    }

    [TestMethod]
    public async Task CambiarLaContrasena_SinLaCredencial_EsInvalidInput()
    {
        var panel = FakePanel.Standard();
        panel.AddServer(Samples.ServiceId, Samples.Paper);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("run_action", Samples.Context(), "k", new JsonObject { ["action"] = "set_panel_password" });

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status);
        CollectionAssert.AreEqual(new[] { "credentials.panel_password" }, PteroHost.Strings(body!["error"]!["details"]!["fields"]));
        Assert.IsEmpty(panel.Requests);
    }
}
