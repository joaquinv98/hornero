using BillingOss.Domain.Catalog;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests;

/// <summary>Una línea de pedido o un servicio con cantidad 0 o negativa bajaría el total sin bajar lo que se aprovisiona.</summary>
[TestClass]
public sealed class QuantityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void LineaDePedido_CantidadMenorQueUno_SeRechaza(int quantity) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Order(1, new Currency("ARS"), Now).AddLine(1, 1, quantity, 100m, 0m));

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Servicio_CantidadMenorQueUno_SeRechaza(int quantity) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Service(1, 1, BillingCycle.Monthly, new Currency("ARS"), 100m, quantity, Now));
}
