using System.Net;
using System.Text;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Página del Banco Nación falsa: devuelve el HTML que le da la prueba, sin salir a la red.</summary>
internal sealed class FakeBna : HttpMessageHandler
{
    public string Html { get; set; } = Page("30/10/2026", "1480,00");

    public int Calls { get; private set; }

    /// <summary>Las dos pestañas de la página, con billetes un poco más caros que divisas, como en la real.</summary>
    public static string Page(string date, string dollarSell, string euroSell = "1720,00") => $"""
        <html><body>
        <ul class="nav nav-tabs"><li><a href="#billetes">Billetes</a></li><li><a href="#divisas">Divisas</a></li></ul>
        <div class="tab-content">
          <div role="tabpanel" class="tab-pane fade in active" id="billetes">
            <table class="table cotizacion">
              <thead><tr><th class="fechaCot">{date}</th><th>Compra</th><th>Venta</th></tr></thead>
              <tbody>
                <tr><td class="tit">Dolar U.S.A</td><td>1450,00</td><td>1500,00</td></tr>
                <tr><td class="tit">Euro</td><td>1690,00</td><td>1760,00</td></tr>
                <tr><td class="tit">Real *</td><td>260,00</td><td>280,00</td></tr>
              </tbody>
            </table>
          </div>
          <div role="tabpanel" class="tab-pane fade" id="divisas">
            <table class="table cotizacion">
              <thead><tr><th class="fechaCot">{date}</th><th>Compra</th><th>Venta</th></tr></thead>
              <tbody>
                <tr><td class="tit">Dolar U.S.A</td><td>1.430,5000</td><td>{dollarSell}</td></tr>
                <tr><td class="tit">Euro</td><td>1650,0000</td><td>{euroSell}</td></tr>
              </tbody>
            </table>
          </div>
        </div>
        </body></html>
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Assert.AreEqual(BnaRates.Page, request.RequestUri, "La prueba solo contesta la página de cotizaciones.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Html, Encoding.UTF8, "text/html") });
    }
}

/// <summary>Cotizaciones del dólar: la fuente del Banco Nación (falsa), la carga manual y cuál vale cada día.</summary>
[TestClass]
public sealed class ExchangeRateTests
{
    internal static Task<CoreHarness> Start(FakeBna bna, bool fetch = true) =>
        CoreHarness.Start(configure: o => o.ExchangeRates.Bna = fetch, services: s => s.AddSingleton(new BnaRates(new HttpClient(bna))));

    internal static Task<Conversion?> Current(CoreHarness h, Currency from, Currency to) =>
        h.Use<ExchangeRateService, Conversion?>(s => s.Current(from, to));

    internal static Task<int> Refresh(CoreHarness h) => h.Use<ExchangeRateService, int>(s => s.Refresh());

    /// <summary>Una cotización ya vigente hoy (2/11/2026 en la prueba): la del banco del viernes 30/10.</summary>
    internal static Task WithDollar(CoreHarness h, decimal sell = 1480m) => h.Db(async db =>
    {
        db.ExchangeRates.Add(new ExchangeRate(Currency.Usd, Currency.Ars, sell, new DateOnly(2026, 10, 30), ExchangeRate.Bna, h.Clock.Now));
        await db.SaveChangesAsync();
    });

    [TestMethod]
    public void Bna_LeeLaTablaDeDivisasYLaDeBilletes()
    {
        var page = FakeBna.Page("30/10/2026", "1.480,5000");

        var divisas = BnaRates.Parse(page, "divisas");
        var billetes = BnaRates.Parse(page, "billetes");

        var dollar = divisas.Single(r => r.Currency == Currency.Usd);
        Assert.AreEqual(new DateOnly(2026, 10, 30), dollar.Date);
        Assert.AreEqual(1430.5m, dollar.Buy);
        Assert.AreEqual(1480.5m, dollar.Sell);
        Assert.AreEqual(1720m, divisas.Single(r => r.Currency.Code == "EUR").Sell);
        Assert.AreEqual(1500m, billetes.Single(r => r.Currency == Currency.Usd).Sell);
        Assert.AreEqual(280m, billetes.Single(r => r.Currency.Code == "BRL").Sell);

        // Sin decimales el punto separa los miles: leerlo como coma cobraría mil veces menos.
        Assert.AreEqual(1480m, BnaRates.Parse(FakeBna.Page("30/10/2026", "1.480"), "divisas").Single(r => r.Currency == Currency.Usd).Sell);
        Assert.AreEqual(1480.5m, BnaRates.Parse(FakeBna.Page("30/10/2026", "1480.50"), "divisas").Single(r => r.Currency == Currency.Usd).Sell);
    }

    [TestMethod]
    public void Bna_UnaPaginaQueNoSeEntiende_FallaALaVista()
    {
        Assert.ThrowsExactly<PermanentFailureException>(() => BnaRates.Parse("<html><p>En mantenimiento</p></html>", "divisas"));
        Assert.ThrowsExactly<PermanentFailureException>(() => BnaRates.Parse("""<div id="divisas"><table><tr><td>Dolar U.S.A</td><td>1</td><td>2</td></tr></table>""", "divisas"));
        Assert.ThrowsExactly<PermanentFailureException>(() => BnaRates.Parse("""<div id="divisas"><table><tr><th>30/10/2026</th></tr><tr><td>Dolar U.S.A</td><td>-</td><td>-</td></tr></table>""", "divisas"));
    }

    [TestMethod]
    public void Cotizacion_SirveEnLosDosSentidos()
    {
        var rate = new ExchangeRate(Currency.Usd, Currency.Ars, 1480m, new DateOnly(2026, 10, 30), ExchangeRate.Bna, DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1480m, rate.Factor(Currency.Usd, Currency.Ars));
        Assert.AreEqual(1m / 1480m, rate.Factor(Currency.Ars, Currency.Usd));
        Assert.ThrowsExactly<InvalidOperationException>(() => rate.Factor(Currency.Usd, new Currency("EUR")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ExchangeRate(Currency.Usd, Currency.Ars, 0m, new DateOnly(2026, 10, 30), ExchangeRate.Bna, DateTimeOffset.UnixEpoch));
        Assert.ThrowsExactly<ArgumentException>(() => new ExchangeRate(Currency.Ars, Currency.Ars, 1m, new DateOnly(2026, 10, 30), ExchangeRate.Bna, DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public async Task Bna_LaDelDiaValeDesdeElSiguiente_YSoloSeGuardaSiCambia()
    {
        var bna = new FakeBna { Html = FakeBna.Page("30/10/2026", "1480,00") };
        await using var h = await Start(bna);

        Assert.AreEqual(1, await Refresh(h));
        Assert.AreEqual(0, await Refresh(h));
        var friday = await Current(h, Currency.Usd, Currency.Ars);
        Assert.AreEqual(1480m, friday!.Factor);
        Assert.AreEqual(ExchangeRate.Bna, friday.Rate.Source);
        Assert.AreEqual(Math.Round(1m / 1480m, 8), (await Current(h, Currency.Ars, Currency.Usd))!.Factor);

        // La del día (lunes 2/11) cambia durante el día y no se usa hasta mañana.
        bna.Html = FakeBna.Page("02/11/2026", "1490,00");
        Assert.AreEqual(1, await Refresh(h));
        bna.Html = FakeBna.Page("02/11/2026", "1495,00");
        Assert.AreEqual(1, await Refresh(h));
        Assert.AreEqual(1480m, (await Current(h, Currency.Usd, Currency.Ars))!.Factor);

        h.Clock.Now = h.Clock.Now.AddDays(1);
        Assert.AreEqual(1495m, (await Current(h, Currency.Usd, Currency.Ars))!.Factor, "Vale el último valor del día: su cierre.");

        // Sin una más nueva, a los cinco días deja de valer y la conversión falla a la vista.
        h.Clock.Now = new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);
        Assert.IsNull(await Current(h, Currency.Usd, Currency.Ars));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, Conversion>(s => s.Require(Currency.Usd, Currency.Ars)));
        Assert.AreEqual(4, bna.Calls);
    }

    [TestMethod]
    public async Task Bna_LasMonedasDeLaConfiguracionNoDistinguenMayusculas()
    {
        var bna = new FakeBna();
        await using var h = await CoreHarness.Start(configure: o =>
        {
            o.ExchangeRates.Bna = true;
            o.ExchangeRates.Currencies = ["usd", "Eur"];
        }, services: s => s.AddSingleton(new BnaRates(new HttpClient(bna))));

        Assert.AreEqual(2, await Refresh(h));
        CollectionAssert.AreEquivalent(new[] { "USD", "EUR" }, (await h.Query(db => db.ExchangeRates.ToListAsync())).Select(r => r.Currency.Code).ToArray());
    }

    [TestMethod]
    public async Task Manual_ValeEseDia_LeGanaALaDelBanco_YQuedaEnLaAuditoria()
    {
        var bna = new FakeBna { Html = FakeBna.Page("30/10/2026", "1480,00") };
        await using var h = await Start(bna);
        await Refresh(h);
        Actor.Current = new Actor(ActorKind.Staff, "staff:3");

        var id = await h.Use<ExchangeRateService, long>(s => s.AddManual("usd", 1510.25m));

        var today = await Current(h, Currency.Usd, Currency.Ars);
        Assert.AreEqual(1510.25m, today!.Factor);
        Assert.AreEqual(id, today.Rate.Id);
        Assert.AreEqual("staff:3", today.Rate.Actor);
        var audit = await h.Query(db => db.Set<AuditEvent>().SingleAsync(a => a.Action == "exchange-rate.manual"));
        Assert.AreEqual("staff:3", audit.Actor);

        // Al día siguiente el banco publica la del 2/11, pero la manual del mismo día sigue ganando hasta una más nueva.
        bna.Html = FakeBna.Page("02/11/2026", "1490,00");
        await Refresh(h);
        h.Clock.Now = h.Clock.Now.AddDays(1);
        Assert.AreEqual(1510.25m, (await Current(h, Currency.Usd, Currency.Ars))!.Factor);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, long>(s => s.AddManual("USD", 0m)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, long>(s => s.AddManual("USD", 1.0000001m)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, long>(s => s.AddManual("ARS", 1m)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, long>(s => s.AddManual("USD", 1500m, new DateOnly(2026, 12, 1))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, long>(s => s.AddManual("dólar", 1500m)));
    }

    [TestMethod]
    public async Task Manual_MasViejaQueElTope_SeRechazaPorqueNoValdria()
    {
        await using var h = await Start(new FakeBna(), fetch: false);

        // Hoy es el 2/11 y el tope es de 5 días: la del 28/10 vale, la del 27/10 ya no valdría para nada.
        await h.Use<ExchangeRateService, long>(s => s.AddManual("USD", 1500m, new DateOnly(2026, 10, 28)));
        Assert.AreEqual(1500m, (await Current(h, Currency.Usd, Currency.Ars))!.Factor);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<ExchangeRateService, long>(s => s.AddManual("USD", 1490m, new DateOnly(2026, 10, 27))));
        Assert.AreEqual(1, await h.Query(db => db.ExchangeRates.CountAsync()));
    }

    [TestMethod]
    public async Task Planificador_TraeLaDelBancoUnaVezPorHora_SoloSiEstaPrendida()
    {
        var off = new FakeBna();
        await using (var h = await Start(off, fetch: false))
        {
            await h.Tick();
            await h.RunJobs();
            Assert.AreEqual(0, off.Calls);
        }

        var bna = new FakeBna();
        await using var on = await Start(bna);
        await on.Tick();
        await on.Tick();
        await on.RunJobs();

        Assert.AreEqual(1, bna.Calls);
        Assert.AreEqual(1, await on.Query(db => db.ExchangeRates.CountAsync()));
        Assert.AreEqual(ExchangeRateService.Entity, await on.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.ExchangeRates).Select(j => j.EntityKey).SingleAsync()));
    }

    [TestMethod]
    public async Task PrecioEnDolares_CuentaEnPesos_CadaFacturaSaleConvertidaConLaCotizacionDeSuDia()
    {
        await using var h = await CoreHarness.Start(configure: o => o.ExchangeRates.IndexPrices = true);
        var plan = await h.Query(async db =>
        {
            var product = new Product { Name = "VPS en dólares", Kind = ProductKind.Manual };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            var monthly = new Plan(product.Id, BillingCycle.Monthly);
            db.Plans.Add(monthly);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(monthly.Id, Currency.Usd, 10m, 2m, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
            return monthly.Id;
        });

        // Sin cotización vigente no se vende: el pedido falla con el motivo y no deja nada a medias.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(plan)])));
        Assert.AreEqual(0, await h.Query(db => db.Services.CountAsync()));

        await WithDollar(h, 1465.5m);
        var placed = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(plan)]));

        var service = await h.Query(db => db.Services.SingleAsync(s => s.Id == placed.ServiceIds[0]));
        Assert.AreEqual(Currency.Ars, service.Currency);
        Assert.AreEqual(Currency.Usd, service.IndexCurrency);
        Assert.AreEqual(10m, service.IndexedUnitPrice);
        Assert.AreEqual(14655m, service.UnitPrice);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(Currency.Ars, invoice.Currency);
        Assert.AreEqual(14655m + 2931m, invoice.Total, "El cargo de alta también sale convertido.");
        StringAssert.Contains(invoice.Lines.Single(l => l.Kind == LineKind.Renewal).Description, "USD 10,00 a 1.465,50 (Banco Nación del 30/10/2026)");

        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, invoice.Total, Domain.Payments.PaymentStatus.Approved)));
        await h.RunJobs();

        // A la renovación la cotización del 30/10 ya venció: sin una nueva el servicio no renueva y la corrida avisa.
        h.Clock.Now = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => h.Use<BillingRunService, int>(s => s.Run()));
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.Invoice)));

        await h.Db(async db =>
        {
            db.ExchangeRates.Add(new ExchangeRate(Currency.Usd, Currency.Ars, 1500m, new DateOnly(2026, 11, 25), ExchangeRate.Bna, h.Clock.Now));
            await db.SaveChangesAsync();
        });
        Assert.AreEqual(1, await h.Use<BillingRunService, int>(s => s.Run()));

        var renewal = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Kind == DocumentKind.Invoice && d.Id != placed.InvoiceId));
        Assert.AreEqual(Currency.Ars, renewal.Currency);
        Assert.AreEqual(15000m, renewal.Total);
        StringAssert.Contains(renewal.Lines.Single().Description, "USD 10,00 a 1.500,00 (Banco Nación del 25/11/2026)");
        Assert.AreEqual(15000m, (await h.Query(db => db.Services.SingleAsync(s => s.Id == service.Id))).UnitPrice, "La mora y los cobros siguen con el precio de la última factura.");

        // Una cuenta en dólares compra el mismo plan en dólares, sin conversión.
        var dollars = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Cliente en dólares", "usd@example.com", Currency.Usd)));
        var usd = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(dollars, [new OrderItem(plan)]));
        var plain = await h.Query(db => db.Services.SingleAsync(s => s.Id == usd.ServiceIds[0]));
        Assert.IsNull(plain.IndexCurrency);
        Assert.AreEqual(10m, plain.UnitPrice);
        Assert.AreEqual(Currency.Usd, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == usd.InvoiceId))).Currency);
    }

    /// <summary>
    /// La página real del Banco Nación. Solo corre con BILLINGOSS_BNA_REAL=1: las pruebas de siempre nunca salen a la red.
    /// </summary>
    [TestMethod]
    public async Task BnaReal_LaPaginaSeLeeYTraeElDolar()
    {
        if (Environment.GetEnvironmentVariable("BILLINGOSS_BNA_REAL") != "1")
        {
            Assert.Inconclusive("Falta BILLINGOSS_BNA_REAL=1: no se consulta la página real del Banco Nación.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var series in (string[])["divisas", "billetes"])
        {
            var dollar = (await new BnaRates(http).Fetch(series, CancellationToken.None)).Single(r => r.Currency == Currency.Usd);
            Assert.IsGreaterThan(dollar.Buy, dollar.Sell, series);
            Assert.IsLessThan(TimeSpan.FromDays(7), DateTime.Today - dollar.Date.ToDateTime(TimeOnly.MinValue), series);
        }
    }
}
