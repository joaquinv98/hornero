using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BillingOss.App.Tests;

/// <summary>
/// Consulta por RDAP con reintentos y el DNS como alternativa, contra un RDAP y un DNS falsos, y las reglas del titular
/// y de los precios. Sin base: corre siempre.
/// </summary>
[TestClass]
public sealed class DomainLookupTests
{
    private static DomainLookup Lookup(FakeRdap rdap, FakeDns? dns = null, int attempts = 3) => new(
        Options.Create(new BillingOptions
        {
            DomainLookup = new DomainLookupOptions
            {
                BootstrapUrl = FakeRdap.Bootstrap,
                Attempts = attempts,
                RetryDelayMilliseconds = 1,
                TimeoutSeconds = 2,
                DnsResolvers = [dns?.Address ?? "127.0.0.1:9"],
            },
        }),
        NullLogger<DomainLookup>.Instance,
        new RdapHandler(rdap));

    [TestMethod]
    public async Task Rdap_404EnHtmlEsLibre_YUnDominioTraeSuVencimiento()
    {
        var rdap = new FakeRdap();
        rdap.Registered["ocupado.com.ar"] = new DateOnly(2027, 9, 3);
        using var lookup = Lookup(rdap);

        var free = await lookup.Lookup("libre.com.ar", CancellationToken.None);
        var taken = await lookup.Lookup("ocupado.com.ar", CancellationToken.None);

        Assert.AreEqual((false, "rdap"), (free.Registered, free.Source));
        Assert.AreEqual((true, "rdap"), (taken.Registered, taken.Source));
        Assert.AreEqual(new DateOnly(2027, 9, 3), taken.ExpiresOn);
        CollectionAssert.Contains(taken.Statuses!.ToList(), "active");
    }

    [TestMethod]
    public async Task Rdap_DosErroresYDespuesContesta_SeReintentaSinPasarAlDns()
    {
        var rdap = new FakeRdap { Failures = 2 };
        rdap.Registered["ocupado.com.ar"] = new DateOnly(2027, 9, 3);
        using var dns = new FakeDns();
        using var lookup = Lookup(rdap, dns);

        var answer = await lookup.Lookup("ocupado.com.ar", CancellationToken.None);

        Assert.AreEqual((true, "rdap"), (answer.Registered, answer.Source));
        Assert.AreEqual(3, rdap.Requests);
    }

    [TestMethod]
    public async Task RdapCaido_ElDnsConfirmaLoDelegado_YNuncaDaPorLibreUnaFalla()
    {
        var rdap = new FakeRdap { Failures = -1 };
        using var dns = new FakeDns();
        dns.Delegated["delegado.com.ar"] = true;
        using var lookup = Lookup(rdap, dns);

        var delegated = await lookup.Lookup("delegado.com.ar", CancellationToken.None);
        var unknown = await lookup.Lookup("sin-delegar.com.ar", CancellationToken.None);
        dns.Silent = true;
        var nothing = await lookup.Lookup("otro.com.ar", CancellationToken.None);

        Assert.AreEqual((true, "dns"), (delegated.Registered, delegated.Source));
        Assert.AreEqual((null, "not_delegated"), (unknown.Registered, unknown.Reason));
        Assert.AreEqual((null, "rdap_unavailable"), (nothing.Registered, nothing.Reason));
        Assert.AreEqual(9, rdap.Requests, "Tres intentos por consulta");
    }

    [TestMethod]
    public async Task Apagado_NoConsultaNada()
    {
        var rdap = new FakeRdap();
        using var lookup = new DomainLookup(
            Options.Create(new BillingOptions { DomainLookup = new DomainLookupOptions { Enabled = false, BootstrapUrl = FakeRdap.Bootstrap } }),
            NullLogger<DomainLookup>.Instance,
            new RdapHandler(rdap));

        var answer = await lookup.Lookup("libre.com.ar", CancellationToken.None);

        Assert.AreEqual((null, "lookup_disabled"), (answer.Registered, answer.Reason));
        Assert.AreEqual(0, rdap.Requests);
    }

    [TestMethod]
    [DataRow("+54 11 5555-1234", "+54.1155551234")]
    [DataRow("011 5555-1234", "+54.1155551234")]
    [DataRow("+54.1155551234", "+54.1155551234")]
    [DataRow("+1 (555) 010-9999", "+1.5550109999")]
    [DataRow("+5491155551234", "+54.91155551234")]
    [DataRow("sin numeros", null)]
    public void Titular_ElTelefonoVaComoLoPidenLosRegistros(string phone, string? expected) =>
        Assert.AreEqual(expected, DomainContacts.Phone(phone));

    [TestMethod]
    public void Titular_SaleDeLaCuentaYDeSuIdentidadFiscal_YElPropioManda()
    {
        var account = new Account("Ana María Gómez", "ana@example.com", Currency.Ars, DateTimeOffset.UnixEpoch) { Phone = "11 5555 1234", CompanyName = "Gómez SRL" };
        var tax = new TaxIdentity(1, TaxDocumentType.Cuit, "20111111112", "Ana María Gómez", 1, "AR", DateTimeOffset.UnixEpoch) { Street = "Calle 1", City = "La Plata", Province = "Buenos Aires", PostalCode = "B1900" };

        var derived = DomainContacts.Registrant(account, tax, null);
        var own = DomainContacts.Registrant(account, tax, """{"first_name":"Otro","last_name":"Titular"}""");

        Assert.AreEqual(("Ana", "María Gómez", "+54.1155551234", "Calle 1", "AR"), (derived["first_name"]!.ToString(), derived["last_name"]!.ToString(), derived["phone"]!.ToString(), derived["address1"]!.ToString(), derived["country"]!.ToString()));
        Assert.IsEmpty(DomainContacts.Missing(derived));
        Assert.AreEqual("Otro", own["first_name"]!.ToString());
        CollectionAssert.AreEquivalent(new[] { "email", "phone", "address1", "city", "postal_code", "country" }, DomainContacts.Missing(own).ToArray());
    }

    [TestMethod]
    public void Precio_CotizacionMargenImpuestoYRedondeoHaciaArriba()
    {
        var rule = new PriceRule(Currency.Ars, ExchangeRate: 1000m, MarginPercent: 30m, TaxPercent: 21m, RoundTo: 100m);

        Assert.AreEqual(15000m, rule.Apply(9.50m));
        Assert.AreEqual(16200m, rule.Apply(10.25m));
        Assert.IsNull(rule.Apply(null));
        Assert.AreEqual(10.01m, new PriceRule(Currency.Usd, RoundTo: 0m).Apply(10.001m));
    }

    [TestMethod]
    public void Extension_SeGuardaEnMinusculasConPuntoYEnPunycode()
    {
        Assert.AreEqual(".com.ar", TldPricing.Tld("COM.AR"));
        Assert.AreEqual(".com", TldPricing.Tld(".com"));
        Assert.AreEqual(".xn--p1ai", TldPricing.Tld("рф"));
        Assert.ThrowsExactly<ValidationException>(() => TldPricing.Tld("no valida"));
    }
}
