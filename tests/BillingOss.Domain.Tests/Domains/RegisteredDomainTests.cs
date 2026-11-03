using BillingOss.Domain.Domains;
using BillingOss.Domain.Shared;

namespace BillingOss.Domain.Tests.Domains;

[TestClass]
public sealed class RegisteredDomainTests
{
    private static RegisteredDomain Domain(DomainStatus status, DateOnly next, DateOnly paid)
    {
        var domain = new RegisteredDomain(1, " Ejemplo.COM.ar ", 1, Currency.Ars, 5000m, DateTimeOffset.UnixEpoch);
        domain.ImportState(status, 1, next, paid);
        return domain;
    }

    [TestMethod]
    public void RenovacionPaga_AvanzaUnAnioSinPasarLoFacturado_YDevuelveElVencimientoARenovar()
    {
        var domain = Domain(DomainStatus.Grace, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1));
        var period = domain.TakeNextPeriod();

        var renewed = domain.RenewalPaid(new DateOnly(2026, 11, 20));
        var again = domain.RenewalPaid(new DateOnly(2026, 11, 20));

        Assert.AreEqual("ejemplo.com.ar", domain.Name);
        Assert.AreEqual(new DateOnly(2027, 11, 1), period.End);
        Assert.AreEqual(new DateOnly(2026, 11, 1), renewed);
        Assert.IsNull(again);
        Assert.AreEqual(new DateOnly(2027, 11, 1), domain.PaidThrough);
        Assert.AreEqual(DomainStatus.Active, domain.Status);
    }

    [TestMethod]
    public void UnVencidoImportadoNoRenueva() =>
        Assert.IsNull(Domain(DomainStatus.Expired, new DateOnly(2023, 5, 1), new DateOnly(2023, 5, 1)).NextDueDate);

    [TestMethod]
    public void Opciones_ConPrecioSumanALaRenovacionUnaVez_YLaBajaSacaSoloLoQueSumo()
    {
        var domain = new RegisteredDomain(1, "ejemplo.com", 1, Currency.Ars, 16000m, DateTimeOffset.UnixEpoch, years: 2) { DnsManagement = false, EmailForwarding = true };

        domain.ContractOption(DomainOptions.Dns, 1590m);
        domain.ContractOption(DomainOptions.Dns, 9999m);
        domain.ContractOption(DomainOptions.Privacy, 2000m);
        Assert.AreEqual((true, 16000m + (2 * (1590m + 2000m))), (domain.DnsManagement, domain.RecurringAmount));
        Assert.IsTrue(domain.HasOption(DomainOptions.Privacy), "La protección contratada cuenta aunque el registrar todavía no la prendió.");

        domain.DropOption(DomainOptions.Dns);
        domain.DropOption(DomainOptions.EmailForwarding);
        domain.DropOption(DomainOptions.Privacy);
        Assert.AreEqual((false, false, false), (domain.DnsManagement, domain.EmailForwarding, domain.HasOption(DomainOptions.Privacy)));
        Assert.AreEqual(16000m, domain.RecurringAmount, "El reenvío marcado sin precio propio no estaba sumado: no se resta.");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => domain.ContractOption("hosting", 1m));
    }

    [TestMethod]
    public void El29DeFebreroRenuevaAl28() =>
        Assert.AreEqual(new DateOnly(2029, 2, 28), Domain(DomainStatus.Active, new DateOnly(2028, 2, 29), new DateOnly(2028, 2, 29)).TakeNextPeriod().End);
}
