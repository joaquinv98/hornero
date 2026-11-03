using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;

namespace BillingOss.Domain.Tests;

[TestClass]
public sealed class StateMachineTests
{
    [TestMethod]
    public void Servicio_SoloSeActivaDesdeAprovisionando()
    {
        Assert.IsTrue(ServiceStates.Machine.CanMove(ServiceStatus.Provisioning, ServiceStatus.Active));
        Assert.IsFalse(ServiceStates.Machine.CanMove(ServiceStatus.Pending, ServiceStatus.Active));
        Assert.IsFalse(ServiceStates.Machine.CanMove(ServiceStatus.Terminated, ServiceStatus.Active));
    }

    [TestMethod]
    public void Pago_AvisoRepetidoNoEsError_YNoVuelveAtras()
    {
        Assert.AreEqual(PaymentStatus.Approved, PaymentStates.Machine.Move(PaymentStatus.Approved, PaymentStatus.Approved));
        Assert.IsFalse(PaymentStates.Machine.CanMove(PaymentStatus.Approved, PaymentStatus.Pending));
        Assert.IsTrue(PaymentStates.Machine.CanMove(PaymentStatus.Disputed, PaymentStatus.Approved));
        Assert.ThrowsExactly<InvalidOperationException>(() => PaymentStates.Machine.Move(PaymentStatus.Refunded, PaymentStatus.Approved));
    }

    [TestMethod]
    public void Emision_IncertidumbreSoloSeResuelveConsultando()
    {
        Assert.IsTrue(EmissionRules.Machine.CanMove(EmissionStatus.Sending, EmissionStatus.Unknown));
        Assert.IsTrue(EmissionRules.Machine.CanMove(EmissionStatus.Unknown, EmissionStatus.Conflict));
        Assert.IsFalse(EmissionRules.Machine.CanMove(EmissionStatus.Unknown, EmissionStatus.Sending));
        Assert.IsFalse(EmissionRules.Machine.CanMove(EmissionStatus.Authorized, EmissionStatus.Pending));
    }

    [TestMethod]
    public void Emision_ElNucleoRechazaSinLlamarYElStaffAdoptaOReintenta()
    {
        // Una nota sin comprobante original se rechaza sin ir al fisco. Un conflicto o una serie fuera de sincronía
        // los resuelve el staff adoptando lo que el fisco tiene o reintentando con otro número.
        Assert.IsTrue(EmissionRules.Machine.CanMove(EmissionStatus.Pending, EmissionStatus.Rejected));
        Assert.IsTrue(EmissionRules.Machine.CanMove(EmissionStatus.Pending, EmissionStatus.Authorized));
        Assert.IsTrue(EmissionRules.Machine.CanMove(EmissionStatus.Conflict, EmissionStatus.Authorized));
        Assert.IsTrue(EmissionRules.Machine.CanMove(EmissionStatus.Conflict, EmissionStatus.Pending));
        Assert.IsFalse(EmissionRules.Machine.CanMove(EmissionStatus.Rejected, EmissionStatus.Authorized));
    }

    [TestMethod]
    [DataRow(10L, 10L, SeriesCheck.InSync)]
    [DataRow(11L, 10L, SeriesCheck.MustReconcile)]
    [DataRow(9L, 10L, SeriesCheck.LocalAhead)]
    public void Serie(long fisco, long local, SeriesCheck expected) =>
        Assert.AreEqual(expected, EmissionRules.CheckSeries(fisco, local));

    [TestMethod]
    public void SerieSincronizada_ValeUnDia()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(EmissionRules.IsSynced(now.AddHours(-23), now));
        Assert.IsFalse(EmissionRules.IsSynced(now.AddHours(-24), now));
        Assert.IsFalse(EmissionRules.IsSynced(null, now));

        // Una marca del futuro (otro nodo con el reloj corrido) no vale.
        Assert.IsFalse(EmissionRules.IsSynced(now.AddMinutes(5), now));
    }

    [TestMethod]
    [DataRow("1450.5", "1450.5", true)]
    [DataRow("29.01", "1450.5", true)]
    [DataRow("5802", "1450.5", true)]
    [DataRow("29.00", "1450.5", false)]
    [DataRow("5802.01", "1450.5", false)]
    [DataRow("1", "1450.5", false)]
    [DataRow("1", "0", false)]
    public void CotizacionDentroDelRangoDelFisco(string rate, string reference, bool expected) =>
        Assert.AreEqual(expected, EmissionRules.RateInRange(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(reference, System.Globalization.CultureInfo.InvariantCulture)));

    [TestMethod]
    public void FechasDelComprobante()
    {
        var hoy = new DateOnly(2026, 10, 27);

        Assert.AreEqual(hoy, EmissionRules.DocumentDate(hoy, new DateOnly(2026, 10, 26)));
        Assert.AreEqual(hoy, EmissionRules.DocumentDate(hoy, null));

        // El fisco acepta hasta diez días después del envío y exige no retroceder: si el último comprobante de la serie
        // es de mañana (otro nodo con el reloj adelantado), se sigue con esa fecha.
        Assert.AreEqual(new DateOnly(2026, 10, 28), EmissionRules.DocumentDate(hoy, new DateOnly(2026, 10, 28)));
        Assert.AreEqual(hoy, EmissionRules.PaymentDue(hoy, new DateOnly(2026, 10, 20)));
        Assert.AreEqual(new DateOnly(2026, 11, 5), EmissionRules.PaymentDue(hoy, new DateOnly(2026, 11, 5)));
    }
}
