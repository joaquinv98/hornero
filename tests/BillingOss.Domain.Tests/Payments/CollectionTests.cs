using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Payments;

[TestClass]
public sealed class CollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("0170099220000012345678", true)]
    [DataRow("0170099120000012345678", false)]
    [DataRow("0170099220000012345679", false)]
    [DataRow("017009922000001234567", false)]
    [DataRow("01700992200000123456a8", false)]
    [DataRow(null, false)]
    public void Cbu_ValidaLosDosDigitosVerificadores(string? cbu, bool valid) =>
        Assert.AreEqual(valid, TransferAccount.IsValidCbu(cbu));

    [TestMethod]
    public void MedioGuardado_SoloGuardaLoQueSeMuestra()
    {
        var method = new SavedPaymentMethod(1, 2, "cus_1", "pm_1", """{"actor":"user:1"}""", Now);

        method.Describe("card", "visa", "4242", 12, 2030, Now);
        Assert.AreEqual("4242", method.Last4);

        // Algo que no son 4 dígitos no se guarda: nunca el número completo.
        method.Describe("card", "visa", "4242424242424242", 13, 1990, Now);
        Assert.IsNull(method.Last4);
        Assert.IsNull(method.ExpMonth);
        Assert.IsNull(method.ExpYear);
    }

    [TestMethod]
    public void MedioGuardado_VenceAFinDelMesDeSuVencimiento()
    {
        var method = new SavedPaymentMethod(1, 2, "cus_1", "pm_1", "{}", Now);
        method.Describe("card", "visa", "4242", 11, 2026, Now);

        Assert.IsFalse(method.ExpiredOn(new DateOnly(2026, 11, 30)));
        Assert.IsTrue(method.ExpiredOn(new DateOnly(2026, 12, 1)));
    }

    [TestMethod]
    public void MedioGuardado_BorradoOVencido_NoSirveParaElDebito()
    {
        var method = new SavedPaymentMethod(1, 2, "cus_1", "pm_1", "{}", Now);
        method.UseForAutoCharge(true, Now);
        Assert.IsTrue(method.AutoCharge);

        method.Expire(Now);
        Assert.IsFalse(method.AutoCharge);
        Assert.ThrowsExactly<InvalidOperationException>(() => method.UseForAutoCharge(true, Now));

        // La red actualizó la tarjeta: vuelve a estar activa, pero el débito lo prende el cliente.
        method.Describe("card", "visa", "4242", 1, 2031, Now);
        Assert.AreEqual(SavedMethodStatus.Active, method.Status);
        Assert.IsFalse(method.AutoCharge);

        method.Delete(Now);
        method.Describe("card", "visa", "4242", 1, 2032, Now);
        Assert.AreEqual(SavedMethodStatus.Deleted, method.Status);
    }

    [TestMethod]
    public void Intento_Rechazado_NoVuelveAtrasSiYaSePago()
    {
        var attempt = PaymentAttempt.Automatic(1, 2, Currency.Ars, 100m, 3, 4, "ref", Now);
        Assert.AreEqual(AttemptInitiator.Merchant, attempt.Initiator);

        attempt.Fail("card_declined:insufficient_funds", "later", Now);
        Assert.AreEqual(AttemptStatus.Failed, attempt.Status);
        Assert.AreEqual("later", attempt.Retry);

        var paid = PaymentAttempt.Automatic(1, 2, Currency.Ars, 100m, 3, 4, "ref2", Now);
        paid.MarkPaid(9, Now);
        paid.Fail("rejected", "never", Now);
        Assert.AreEqual(AttemptStatus.Paid, paid.Status);
    }

    [TestMethod]
    public void PagoDePrueba_SoloObservaYNoTieneCuenta()
    {
        var attempt = PaymentAttempt.Test(2, Currency.Usd, 1m, "ref", Now);
        Assert.IsNull(attempt.AccountId);

        attempt.Observe("pending", Now);
        Assert.AreEqual(AttemptStatus.Open, attempt.Status);
        attempt.Observe("approved", Now);
        Assert.AreEqual(AttemptStatus.Paid, attempt.Status);
        Assert.IsNull(attempt.PaymentId);
    }

    [TestMethod]
    public void Intento_SinMontoPositivo_NoSeAbre() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PaymentAttempt(1, 2, Currency.Ars, 0m, [3], "ref", Now));

    [TestMethod]
    public void Suscripcion_CanceladaNoVuelve()
    {
        var subscription = new GatewaySubscription(1, 2, Currency.Ars, 12100m, "ref", "{}", Now);

        Assert.IsTrue(subscription.Observe(SubscriptionStatus.Active, Now));
        Assert.IsFalse(subscription.Observe(SubscriptionStatus.Active, Now));
        Assert.IsTrue(subscription.Observe(SubscriptionStatus.Cancelled, Now));
        Assert.IsFalse(subscription.Observe(SubscriptionStatus.Active, Now));
        Assert.AreEqual(SubscriptionStatus.Cancelled, subscription.Status);
    }

    [TestMethod]
    public void Pasarela_OfreceSoloLasMonedasActivadas()
    {
        var setup = new GatewaySetup(2, "Stripe", Now) { Currencies = ["USD"] };

        Assert.IsFalse(setup.Offers(Currency.Usd));
        setup.Enabled = true;
        Assert.IsTrue(setup.Offers(Currency.Usd));
        Assert.IsFalse(setup.Offers(Currency.Ars));
    }
}
