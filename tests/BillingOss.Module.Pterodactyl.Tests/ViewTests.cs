using System.Net;
using System.Text.Json.Nodes;

namespace BillingOss.Module.Pterodactyl.Tests;

/// <summary>Vista del servicio en el portal (PTR-12).</summary>
[TestClass]
public sealed class ViewTests
{
    [TestMethod]
    public async Task ServicioActivo_MuestraLaDireccion_ElEstadoYElBotonAlPanel()
    {
        var panel = FakePanel.Standard();
        var server = panel.AddServer(Samples.ServiceId, Samples.Paper, port: 25570);
        await using var host = await PteroHost.Start(panel);

        var (status, body) = await host.Call("view", Samples.Context());

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var blocks = Blocks(body);
        Assert.AreEqual("ok", blocks["status"]["level"]!.GetValue<string>());
        Assert.AreEqual("Activo", blocks["status"]["text"]!.GetValue<string>());
        var field = blocks["fields"]["items"]![0]!;
        Assert.AreEqual("Dirección", field["label"]!.GetValue<string>());
        Assert.AreEqual("mc.example.test:25570", field["value"]!.GetValue<string>());
        Assert.AreEqual("Ir al panel", blocks["link"]["label"]!.GetValue<string>());
        Assert.AreEqual($"https://panel.example.test/server/{server.Identifier}", blocks["link"]["url"]!.GetValue<string>());
        Assert.AreEqual(0, panel.Writes);
    }

    [TestMethod]
    public async Task EnIngles_YMientrasInstala()
    {
        var panel = FakePanel.Standard();
        panel.AddServer(Samples.ServiceId, Samples.Paper, status: "installing");
        await using var host = await PteroHost.Start(panel);

        var (_, body) = await host.Call("view", Samples.Context(account: Samples.Account(language: "en-US")));

        var blocks = Blocks(body);
        Assert.AreEqual("warning", blocks["status"]["level"]!.GetValue<string>());
        Assert.AreEqual("Installing", blocks["status"]["text"]!.GetValue<string>());
        Assert.AreEqual("Address", blocks["fields"]["items"]![0]!["label"]!.GetValue<string>());
        Assert.AreEqual("Go to panel", blocks["link"]["label"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PanelCaido_MuestraUnAviso_YLaDireccionGuardada()
    {
        var panel = FakePanel.Standard();
        panel.On("GET servers/external/1042", (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "rechazada"));
        await using var host = await PteroHost.Start(panel);
        var context = Samples.Context(externalIds: new JsonObject { ["address"] = "mc.example.test:25570", ["identifier"] = "abcd1234" });

        var (status, body) = await host.Call("view", context);

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        var blocks = Blocks(body);
        Assert.AreEqual("warning", blocks["notice"]["level"]!.GetValue<string>());
        Assert.AreEqual("mc.example.test:25570", blocks["fields"]["items"]![0]!["value"]!.GetValue<string>());
        Assert.AreEqual("https://panel.example.test/server/abcd1234", blocks["link"]["url"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task SinServer_AvisaYElBotonVaALaRaizDelPanel()
    {
        await using var host = await PteroHost.Start(FakePanel.Standard());

        // Lo guardado de un server que ya no existe no se muestra.
        var (_, body) = await host.Call("view", Samples.Context(externalIds: new JsonObject { ["address"] = "mc.example.test:25570", ["identifier"] = "abcd1234" }));

        var blocks = Blocks(body);
        Assert.AreEqual("error", blocks["notice"]["level"]!.GetValue<string>());
        Assert.IsFalse(blocks.ContainsKey("fields"));
        Assert.AreEqual("https://panel.example.test/", blocks["link"]["url"]!.GetValue<string>());
    }

    /// <summary>Los bloques por tipo. Cada vista tiene a lo sumo uno de cada uno.</summary>
    private static Dictionary<string, JsonNode> Blocks(JsonNode? body) =>
        body!["result"]!["blocks"]!.AsArray().ToDictionary(b => b!["type"]!.GetValue<string>(), b => b!);
}
