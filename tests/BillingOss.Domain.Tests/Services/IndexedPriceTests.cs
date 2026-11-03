using BillingOss.Domain.Catalog;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Services;

/// <summary>Precio pactado en otra moneda: la factura sigue en la del servicio y cada renovación lo convierte.</summary>
[TestClass]
public sealed class IndexedPriceTests
{
    private static Service Pesos() => new(1, 1, BillingCycle.Monthly, Currency.Ars, 14655m, 1, DateTimeOffset.UnixEpoch);

    [TestMethod]
    public void PactadoEnDolares_LaRenovacionCambiaElPrecioEnPesos_YElPactadoQueda()
    {
        var service = Pesos();
        service.IndexTo(Currency.Usd, 10m);

        service.Reprice(15000m);

        Assert.AreEqual(15000m, service.UnitPrice);
        Assert.AreEqual(15000m, service.RecurringAmount);
        Assert.AreEqual(Currency.Usd, service.IndexCurrency);
        Assert.AreEqual(10m, service.IndexedUnitPrice);
        Assert.AreEqual(Currency.Ars, service.Currency);
    }

    [TestMethod]
    public void SinPrecioPactado_NoSeConvierte_NiEnLaMismaMoneda()
    {
        var service = Pesos();

        Assert.ThrowsExactly<InvalidOperationException>(() => service.Reprice(15000m));
        Assert.ThrowsExactly<ArgumentException>(() => service.IndexTo(Currency.Ars, 10m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => service.IndexTo(Currency.Usd, -1m));
        Assert.IsNull(service.IndexCurrency);
    }
}
