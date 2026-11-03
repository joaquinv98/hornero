using BillingOss.App.UseCases;

namespace BillingOss.App.Tests;

/// <summary>
/// Prueba contra el VIES real de la Comisión Europea. Solo corre con VIES_LIVE_COUNTRY y VIES_LIVE_NUMBER (un número de
/// IVA válido de la UE, por ejemplo el de un proveedor conocido). Sin ellas queda inconclusa: ningún test llama a un
/// sistema real por defecto.
/// </summary>
[TestClass]
public sealed class ViesLiveTests
{
    [TestMethod]
    [TestCategory("Vivo")]
    public async Task Vies_ValidaUnNumeroReal_YDevuelveElNumeroDeConsulta()
    {
        var country = Environment.GetEnvironmentVariable("VIES_LIVE_COUNTRY");
        var number = Environment.GetEnvironmentVariable("VIES_LIVE_NUMBER");
        if (string.IsNullOrEmpty(country) || string.IsNullOrEmpty(number))
        {
            Assert.Inconclusive("Falta VIES_LIVE_COUNTRY o VIES_LIVE_NUMBER: no se llama al VIES real.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new ViesClient(http, TimeProvider.System, Environment.GetEnvironmentVariable("VIES_LIVE_REQUESTER_COUNTRY"), Environment.GetEnvironmentVariable("VIES_LIVE_REQUESTER_NUMBER"));

        var check = await client.Check(country!, number!, CancellationToken.None);

        Assert.IsTrue(check.Valid);
        Assert.IsFalse(string.IsNullOrEmpty(check.Name));
    }
}
