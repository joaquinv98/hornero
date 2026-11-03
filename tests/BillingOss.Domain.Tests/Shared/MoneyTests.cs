using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Shared;

[TestClass]
public sealed class MoneyTests
{
    [TestMethod]
    public void SumaMismaMoneda() =>
        Assert.AreEqual(new Money(3.50m, Currency.Ars), new Money(1.25m, Currency.Ars) + new Money(2.25m, Currency.Ars));

    [TestMethod]
    public void NoMezclaMonedas() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => new Money(1m, Currency.Ars) + new Money(1m, Currency.Usd));

    [TestMethod]
    [DataRow(2.345, 2.35)]
    [DataRow(2.344, 2.34)]
    [DataRow(-2.345, -2.35)]
    public void RedondeoComercial(double value, double expected) =>
        Assert.AreEqual((decimal)expected, new Money((decimal)value, Currency.Usd).Round().Amount);

    [TestMethod]
    [DataRow("ars")]
    [DataRow("PESO")]
    [DataRow("")]
    public void RechazaMonedaInvalida(string code) =>
        Assert.ThrowsExactly<ArgumentException>(() => new Currency(code));
}
