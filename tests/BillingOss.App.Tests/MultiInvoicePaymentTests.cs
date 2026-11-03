using System.Text.Json.Nodes;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Varias facturas en un solo pago, y facturas en dólares cobradas en pesos con la cotización del intento.</summary>
[TestClass]
public sealed class MultiInvoicePaymentTests
{
    private static async Task<(CoreHarness H, TestModule Module, FakeGateway Gateway)> Start(string[]? currencies = null, Action<BillingOptions>? configure = null)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, configure);
        var gateway = new FakeGateway { Currencies = currencies };
        var module = await TestModule.Start(gateway.Definition(), h, box);
        await h.Db(async db =>
        {
            (await db.ModuleInstances.SingleAsync()).BrowserHosts = """["pay.example.com"]""";
            await db.SaveChangesAsync();
        });
        await FinalDollars(h);
        return (h, module, gateway);
    }

    /// <summary>
    /// Los importes de estas pruebas son finales, para seguir la cuenta de la conversión: el dólar se carga con IVA
    /// incluido, como el peso, y no sin él como viene de fábrica.
    /// </summary>
    private static Task<bool> FinalDollars(CoreHarness h) => h.Use<TaxSetupService, bool>(async s =>
    {
        await s.SetDefaultPriceMode(Currency.Usd, PriceMode.TaxIncluded);
        return true;
    });

    private static Task<long> Usd(CoreHarness h, decimal price, string description = "VPS en dólares") =>
        AdvancedBillingTests.Manual(h, [new LineInput(description, 1m, price)], currency: "USD");

    private static Task<CheckoutStarted> Checkout(CoreHarness h, TestModule module, params long[] ids) =>
        h.Use<GatewayPayments, CheckoutStarted>(g => g.Checkout(h.AccountId, ids, module.InstanceId));

    private static async Task Pay(CoreHarness h, TestModule module, FakeGateway gateway, string reference, string amount, string id = "p-1")
    {
        gateway.Add(id, reference, amount);
        Assert.IsTrue(await GatewayFlowTests.Notify(h, module, $"evt-{id}", id));
        await h.RunJobs();
    }

    [TestMethod]
    public async Task CargaDeSaldo_UnIntentoSinFacturas_ElPagoQuedaEnteroComoSaldo()
    {
        var (h, module, gateway) = await Start(["ARS"], o => o.TopUp["ars"] = new TopUpLimits { Min = 1000m, Max = 50000m });
        await using var _ = h;
        await using var __ = module;
        var invoice = await AdvancedBillingTests.Manual(h, [new LineInput("Horas de soporte", 1m, 3000m)]);

        var started = await h.Use<GatewayPayments, CheckoutStarted>(g => g.TopUp(h.AccountId, "ARS", 5000m, module.InstanceId));

        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.IsEmpty(attempt.DocumentIds);
        Assert.AreEqual(5000m, attempt.Amount);
        Assert.AreEqual("credit", gateway.Checkouts.Single().Input["items"]![0]!["id"]!.GetValue<string>());

        await Pay(h, module, gateway, started.Reference, "5000.00");

        Assert.AreEqual(5000m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == h.AccountId).SumAsync(b => b.Balance)));
        Assert.AreEqual(3000m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Pending, "La carga no se imputa sola a lo que se debe.");
        Assert.AreEqual(AttemptStatus.Paid, (await h.Query(db => db.PaymentAttempts.SingleAsync())).Status);

        Task<CheckoutStarted> TopUp(string currency, decimal amount) => h.Use<GatewayPayments, CheckoutStarted>(g => g.TopUp(h.AccountId, currency, amount, module.InstanceId));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => TopUp("ARS", 999.99m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => TopUp("ARS", 50000.01m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => TopUp("ARS", 1500.005m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => TopUp("USD", 100m));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<GatewayPayments, CheckoutStarted>(g => g.TopUp(h.AccountId, "ARS", 2000m, 999999)));
        Assert.AreEqual(1, await h.Query(db => db.PaymentAttempts.CountAsync()));
    }

    [TestMethod]
    public void MonedaDelCobro_SaleDelManifiestoDeLaPasarela()
    {
        JsonObject Manifest(params string[]? currencies) => currencies is null ? [] : new JsonObject { ["currencies"] = new JsonArray([.. currencies.Select(c => (JsonNode)c)]) };

        Assert.AreEqual(Currency.Usd, GatewayPayments.ChargeCurrency(Manifest(null), Currency.Usd));
        Assert.AreEqual(Currency.Ars, GatewayPayments.ChargeCurrency(Manifest("ARS", "BRL"), Currency.Usd));
        Assert.AreEqual(Currency.Usd, GatewayPayments.ChargeCurrency(Manifest("ARS", "USD"), Currency.Usd));
        Assert.AreEqual(Currency.Usd, GatewayPayments.ChargeCurrency(Manifest("*"), Currency.Usd));
        Assert.AreEqual(Currency.Ars, GatewayPayments.ChargeCurrency(Manifest("*"), Currency.Usd, Currency.Ars));
        Assert.ThrowsExactly<ValidationException>(() => GatewayPayments.ChargeCurrency(Manifest("ARS"), Currency.Usd, Currency.Usd));
        Assert.ThrowsExactly<ValidationException>(() => GatewayPayments.ChargeCurrency(Manifest(), Currency.Usd));
    }

    [TestMethod]
    public async Task PagarTodo_UnIntentoPorVariasFacturas_LasDejaPagasConUnSoloPago()
    {
        var (h, module, gateway) = await Start();
        await using var _ = h;
        await using var __ = module;
        var first = await AdvancedBillingTests.Manual(h, [new LineInput("Horas de soporte", 2m, 1000m)], due: new DateOnly(2026, 11, 20));
        var second = await AdvancedBillingTests.Manual(h, [new LineInput("Disco", 1m, 500m)], due: new DateOnly(2026, 11, 10));

        var started = await Checkout(h, module, first, second);

        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(2500m, attempt.Amount);
        Assert.IsNull(attempt.Rate);
        CollectionAssert.AreEqual(new[] { second, first }, attempt.DocumentIds.ToArray(), "Primero la que vence antes.");
        var items = gateway.Checkouts.Single().Input["items"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "500.00", "2000.00" }, items.Select(i => i!["unit_price"]!.GetValue<string>()).ToArray());

        await Pay(h, module, gateway, started.Reference, "2500.00");

        var docs = await h.Query(db => db.Documents.Where(d => d.Id == first || d.Id == second).ToListAsync());
        Assert.IsTrue(docs.All(d => d.Status == InvoiceStatus.Paid));
        Assert.AreEqual(2, await h.Query(db => db.Allocations.CountAsync(a => a.PaymentId != null)));
    }

    [TestMethod]
    public async Task FacturasEnDolares_LaPasarelaEnPesosCobraConvertido_YElPagoLasDejaPagas()
    {
        var (h, module, gateway) = await Start(["ARS"]);
        await using var _ = h;
        await using var __ = module;
        await ExchangeRateTests.WithDollar(h, 1480.333333m);
        var vps = await Usd(h, 24m);
        var domain = await Usd(h, 10.5m, "Dominio en dólares");

        var started = await Checkout(h, module, vps, domain);

        // 34,50 dólares a 1.480,333333: 51.071,50 pesos, repartidos en forma acumulada para que los renglones sumen eso.
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(Currency.Ars, attempt.Currency);
        Assert.AreEqual(51071.50m, attempt.Amount);
        Assert.AreEqual(1480.333333m, attempt.Rate);
        Assert.IsNotNull(attempt.ExchangeRateId);
        var input = gateway.Checkouts.Single().Input;
        Assert.AreEqual("ARS", input["amount"]!["currency"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "35528.00", "15543.50" }, input["items"]!.AsArray().Select(i => i!["unit_price"]!.GetValue<string>()).ToArray());

        await Pay(h, module, gateway, started.Reference, "51071.50");

        var docs = await h.Query(db => db.Documents.Where(d => d.Id == vps || d.Id == domain).OrderBy(d => d.Id).ToListAsync());
        Assert.IsTrue(docs.All(d => d.Status == InvoiceStatus.Paid && d.Currency == Currency.Usd));
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(Currency.Ars, payment.Currency);
        Assert.AreEqual(51071.50m, payment.Applied, "El pago se consume entero, sin centavos sueltos.");
        var allocations = await h.Query(db => db.Allocations.Where(a => a.PaymentId == payment.Id).OrderBy(a => a.DocumentId).ToListAsync());
        CollectionAssert.AreEqual(new[] { 24m, 10.5m }, allocations.Select(a => a.Amount).ToArray());
        CollectionAssert.AreEqual(new decimal?[] { 35528.00m, 15543.50m }, allocations.Select(a => a.PaymentAmount).ToArray());
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.SumAsync(b => b.Balance)));
    }

    [TestMethod]
    public async Task FacturaEnPesos_LaPasarelaEnDolaresCobraConvertido_YElPagoLaDejaPaga()
    {
        var (h, module, gateway) = await Start(["USD"]);
        await using var _ = h;
        await using var __ = module;
        await ExchangeRateTests.WithDollar(h, 1465.5m);
        var invoice = await AdvancedBillingTests.Manual(h, [new LineInput("Horas de soporte", 1m, 10000m)]);

        var started = await Checkout(h, module, invoice);

        // 10.000 pesos son 6,82 dólares. De vuelta a pesos, un centavo de dólar vale casi 15 pesos: la factura se salda
        // con lo que congeló el intento y no con la cuenta inversa, que daría 9.994,72.
        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(Currency.Usd, attempt.Currency);
        Assert.AreEqual(6.82m, attempt.Amount);
        gateway.Add("p-1", started.Reference, "6.82");
        foreach (var part in new[] { "amount", "refunded", "fee" })
        {
            gateway.Payments["p-1"][part]!["currency"] = "USD";
        }

        gateway.Payments["p-1"]["fee"]!["value"] = "0.30";
        Assert.IsTrue(await GatewayFlowTests.Notify(h, module, "evt-p-1", "p-1"));
        await h.RunJobs();

        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Status);
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(Currency.Usd, payment.Currency);
        Assert.AreEqual(6.82m, payment.Applied);
        Assert.AreEqual(6.82m, (await h.Query(db => db.Allocations.SingleAsync(a => a.PaymentId == payment.Id))).PaymentAmount);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.SumAsync(b => b.Balance)));
    }

    [TestMethod]
    public async Task FacturaEnDolares_UnPagoQueNoCoincideConElIntento_QuedaComoSaldoEnPesos()
    {
        var (h, module, gateway) = await Start(["ARS"]);
        await using var _ = h;
        await using var __ = module;
        await ExchangeRateTests.WithDollar(h);
        var vps = await Usd(h, 24m);
        var started = await Checkout(h, module, vps);

        await Pay(h, module, gateway, started.Reference, "30000.00");

        Assert.AreEqual(24m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == vps))).Pending);
        Assert.AreEqual(30000m, await h.Query(db => db.CreditBalances.Where(b => b.Currency == Currency.Ars).SumAsync(b => b.Balance)));
    }

    [TestMethod]
    public async Task FacturaEnDolares_SinCotizacionVigente_NoSeAbreElIntento()
    {
        var (h, module, _) = await Start(["ARS"]);
        await using var __ = h;
        await using var ___ = module;
        var vps = await Usd(h, 24m);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Checkout(h, module, vps));
        Assert.AreEqual(0, await h.Query(db => db.PaymentAttempts.CountAsync()));
    }

    [TestMethod]
    public async Task PagoEnPesosConvertido_ContracargoDevuelveLasFacturasADeber()
    {
        var (h, module, gateway) = await Start(["ARS"]);
        await using var _ = h;
        await using var __ = module;
        await ExchangeRateTests.WithDollar(h);
        var vps = await Usd(h, 24m);
        var domain = await Usd(h, 10.5m);
        var started = await Checkout(h, module, vps, domain);
        await Pay(h, module, gateway, started.Reference, "51060.00");

        gateway.Payments["p-1"]["status"] = "charged_back";
        Assert.IsTrue(await GatewayFlowTests.Notify(h, module, "evt-cb", "p-1"));
        await h.RunJobs();

        var docs = await h.Query(db => db.Documents.Where(d => d.Id == vps || d.Id == domain).ToListAsync());
        Assert.IsTrue(docs.All(d => d.Allocated == 0m && d.Status == InvoiceStatus.Issued));
        var payment = await h.Query(db => db.Payments.SingleAsync());
        Assert.AreEqual(PaymentStatus.ChargedBack, payment.Status);
        Assert.AreEqual(0m, payment.Applied);
        Assert.AreEqual(0m, (await h.Query(db => db.Refunds.SingleAsync())).Uncovered);
    }

    [TestMethod]
    public async Task PagoManualEnPesos_DeUnaFacturaEnDolares_SeImputaConLaCotizacionDadaOLaVigente()
    {
        await using var h = await CoreHarness.Start();
        await FinalDollars(h);
        await ExchangeRateTests.WithDollar(h, 1500m);
        var given = await Usd(h, 10m);
        var current = await Usd(h, 20m);

        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 14000m, PaymentStatus.Approved, [given], Rate: 1400m)));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 31000m, PaymentStatus.Approved, [current])));

        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == given))).Status);
        Assert.AreEqual(InvoiceStatus.Paid, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == current))).Status);
        Assert.AreEqual(1000m, await h.Query(db => db.CreditBalances.Where(b => b.Currency == Currency.Ars).SumAsync(b => b.Balance)), "Lo que sobra al cambio de hoy queda en pesos.");
        var rates = await h.Query(db => db.Allocations.OrderBy(a => a.Id).Select(a => new { a.Rate, a.PaymentAmount }).ToListAsync());
        Assert.AreEqual(Math.Round(10m / 14000m, 8), rates[0].Rate);
        Assert.AreEqual(30000m, rates[1].PaymentAmount);
    }

    [TestMethod]
    public async Task PagoManual_ConUnaCotizacionQueNoSirveOSinNinguna_NoSeRegistra()
    {
        await using var h = await CoreHarness.Start();
        await FinalDollars(h);
        var vps = await Usd(h, 10m);
        Task Pay(string id, decimal? rate) => h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", id, Currency.Ars, 14000m, PaymentStatus.Approved, [vps], Rate: rate)));

        // Cero, negativa o con más de ocho decimales: la factura se salteaba y los pesos iban enteros a saldo sin aviso.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Pay("T-1", 0m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Pay("T-2", -1400m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Pay("T-3", 1400.123456789m));

        // Sin cotización dada ni vigente, imputar a una factura en dólares es un error del pedido y no un saldo a favor.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Pay("T-4", null));

        Assert.AreEqual(0, await h.Query(db => db.Payments.CountAsync()));
        Assert.AreEqual(10m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == vps))).Pending);
    }
}
