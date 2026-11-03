using BillingOss.App.Identity;
using BillingOss.Web.Identity;

namespace BillingOss.App.Tests;

/// <summary>
/// Prueba contra Google real (diseno/ingreso-clientes.md, sección 9). Solo corre con BILLINGOSS_GOOGLE_CLIENT_ID y
/// BILLINGOSS_GOOGLE_CLIENT_SECRET, de un cliente OAuth de prueba: lee la configuración de Google y canjea
/// un código inventado, que Google rechaza como inválido solo si reconoció el cliente y el secreto. Sin ellas queda
/// inconclusa: ningún test llama a un sistema real por defecto.
/// </summary>
[TestClass]
public sealed class GoogleLiveTests
{
    [TestMethod]
    [TestCategory("Vivo")]
    public async Task Google_ReconoceElClienteYElSecreto()
    {
        var clientId = Environment.GetEnvironmentVariable("BILLINGOSS_GOOGLE_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("BILLINGOSS_GOOGLE_CLIENT_SECRET");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
        {
            Assert.Inconclusive("Falta BILLINGOSS_GOOGLE_CLIENT_ID o BILLINGOSS_GOOGLE_CLIENT_SECRET: no se llama a Google.");
        }

        await using var h = await UiHarness.Start();
        await h.Use<ExternalLoginStore, long>(s => s.Save(ExternalProvider.GoogleKey, ExternalProviderKind.Google, new("Google", string.Empty, clientId!, secret!, true), 0));
        var provider = await h.Use<ExternalLoginStore, ExternalProvider?>(s => s.Find(ExternalProvider.GoogleKey));

        Assert.AreEqual("External.TestOk", await h.Use<ExternalSignIn, string>(s => s.Test(provider!, CancellationToken.None)));
    }
}
