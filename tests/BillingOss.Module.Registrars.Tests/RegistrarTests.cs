using System.Net;
using System.Text.Json.Nodes;
using BillingOss.Modules.Protocol;

namespace BillingOss.Module.Registrars.Tests;

/// <summary>Lo propio de cada registrar que no cubren los escenarios comunes.</summary>
[TestClass]
public sealed class RegistrarTests
{
    [TestMethod]
    public async Task OpenProvider_ElTokenSeGuardaComoEstadoYSeUsaEnLasLlamadasSiguientes()
    {
        await using var h = await RegistrarHost.Start("openprovider");
        var fake = (FakeOpenProvider)h.Fake;
        h.Registry.Add("ejemplo.com", h.Registry.Today.AddYears(1));

        await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("libre.com") });
        await h.Send("/v0/registrar/get_domain");
        var (status, _) = await h.Send("/v0/registrar/set_lock", new JsonObject { ["locked"] = false }, "dom-1-set_lock-1");

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(1, fake.Logins);
        Assert.AreEqual("tok-1", h.State!["token"]!.ToString());
    }

    [TestMethod]
    public async Task OpenProvider_UnTokenVencido_EntraDeNuevoUnaSolaVez()
    {
        await using var h = await RegistrarHost.Start("openprovider");
        var fake = (FakeOpenProvider)h.Fake;
        await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("libre.com") });

        fake.ExpireTokens();
        var (status, body) = await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("libre.com") });

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(2, fake.Logins);
        Assert.AreEqual("tok-2", h.State!["token"]!.ToString());
    }

    [TestMethod]
    public async Task ResellerClub_RenuevaConElVencimientoQueTieneElRegistro_NoConElDelNucleo()
    {
        await using var h = await RegistrarHost.Start("resellerclub");
        var expires = h.Registry.Today.AddDays(10);
        h.Registry.Add("ejemplo.com", expires);

        // El núcleo cree que vence otro día (la zona horaria corre un día la fecha): LogicBoxes rechazaría ese exp-date.
        var core = expires.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var (status, body) = await h.Send("/v0/registrar/renew", new JsonObject { ["years"] = 2, ["current_expiry"] = core }, "dom-1-renew-1");

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(expires.AddYears(2), h.Registry.Mine["ejemplo.com"].Expires);
    }

    [TestMethod]
    public async Task ResellerClub_LaConsultaPorNombreAgrupaLosTldDeUnaMismaEtiqueta()
    {
        await using var h = await RegistrarHost.Start("resellerclub");

        var (status, body) = await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("libre.com", "libre.net", "tomado.net") });

        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.AreEqual(3, body!["result"]!["results"]!.AsArray().Count);
        Assert.AreEqual(2, h.Fake.Requests);
    }

    [TestMethod]
    public async Task Namecheap_LeeElXmlConSuEspacioDeNombres_YAgrupaLaConsulta()
    {
        await using var h = await RegistrarHost.Start("namecheap");

        var (status, body) = await h.Send("/v0/registrar/check", new JsonObject { ["names"] = new JsonArray("libre.com", "tomado.com", "otro.net") });

        Assert.AreEqual(HttpStatusCode.OK, status, body?.ToJsonString());
        Assert.AreEqual(1, h.Fake.Requests);
        CollectionAssert.AreEqual(new bool?[] { true, false, true }, body!["result"]!["results"]!.AsArray().Select(r => r!["available"]?.GetValue<bool>()).ToArray());
    }

    [TestMethod]
    public void RegistrarDesconocido_NoArranca()
    {
        using var http = new HttpClient();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => RegistrarModule.Create("otro", http, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        StringAssert.Contains(error.Message, "internetbs");
    }

    [TestMethod]
    [DataRow("ejemplo.com", "ejemplo.com")]
    [DataRow(" Ejemplo.COM. ", "ejemplo.com")]
    [DataRow("ñandú.com.ar", "xn--and-6ma2c.com.ar")]
    [DataRow("sin-extension", null)]
    [DataRow("-guion.com", null)]
    [DataRow("con espacio.com", null)]
    [DataRow("123.456", null)]
    public void NombresDeDominio_SeNormalizanComoEnElProtocolo(string text, string? expected)
    {
        var ok = DomainNames.TryNormalize(text, out var name);

        Assert.AreEqual(expected is not null, ok);
        if (expected is not null)
        {
            Assert.AreEqual(expected, name);
        }
    }
}
