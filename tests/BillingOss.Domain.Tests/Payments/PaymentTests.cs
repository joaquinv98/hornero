using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Payments;

[TestClass]
public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Importes_ConMasDeDosDecimales_NoEntran()
    {
        // La base guarda dos decimales: 100.005 se redondearía a 100.01 y acreditaría medio centavo que nadie pagó.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Payment(1, "manual:x", "T-1", Currency.Ars, 100.005m, PaymentStatus.Approved, Now));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Payment(1, "manual:x", "T-1", Currency.Ars, 100m, PaymentStatus.Approved, Now, fee: 0.001m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Allocation(1, 0.005m, 1m, Now, paymentId: 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CreditEntry(1, Currency.Ars, 0.005m, "Prueba", Now));
        Assert.AreEqual(100.01m, new Payment(1, "manual:x", "T-1", Currency.Ars, 100.01m, PaymentStatus.Approved, Now).Amount);
    }
}
