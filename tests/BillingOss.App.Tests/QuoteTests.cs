using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Presupuestos: se arman, se mandan y, aceptados, se convierten una sola vez en factura o en pedido.</summary>
[TestClass]
public sealed class QuoteTests
{
    private static readonly QuoteLine Migration = new("Migración de 3 sitios", 1m, 30000m);

    private static readonly QuoteLine Disk = new("Disco extra", 2m, 2500m, TaxTreatment.Exempt);

    internal static Task<long> Create(CoreHarness h, IReadOnlyList<QuoteLine> lines, DateOnly? validUntil = null, string? currency = null, long? accountId = null) =>
        h.Use<QuoteService, long>(s => s.Create(new QuoteInput(accountId ?? h.AccountId, "Mudanza de servidor", lines, validUntil, currency, "Incluye una hora de soporte.")));

    private static Task<bool> Run(CoreHarness h, Func<QuoteService, Task> work) => h.Use<QuoteService, bool>(async s =>
    {
        await work(s);
        return true;
    });

    private static Task<Quote> Get(CoreHarness h, long id) => h.Query(db => db.Quotes.SingleAsync(q => q.Id == id));

    [TestMethod]
    public async Task Presupuesto_SeArmaConElImpuestoDelReceptor_YSeEditaMientrasEstaAbierto()
    {
        await using var h = await CoreHarness.Start();

        var id = await Create(h, [Migration, Disk]);

        var quote = await Get(h, id);
        Assert.AreEqual(QuoteStatus.Draft, quote.Status);
        Assert.AreEqual(35000m, quote.Total);
        Assert.AreEqual(new DateOnly(2026, 12, 2), quote.ValidUntil, "Sin vigencia, la de siempre: 30 días.");
        Assert.AreEqual(Currency.Ars, quote.Currency);
        CollectionAssert.AreEqual(new[] { Migration, Disk }, quote.Lines.ToArray());

        await Run(h, s => s.Update(id, new QuoteInput(h.AccountId, "Mudanza de servidor", [Migration], new DateOnly(2026, 11, 20), "USD")));
        quote = await Get(h, id);
        Assert.AreEqual(36300m, quote.Total, "En dólares los precios se cargan sin IVA, el modo de carga de la moneda.");
        Assert.AreEqual(Currency.Usd, quote.Currency);
        Assert.IsNull(quote.Notes);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, []));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [Migration], validUntil: new DateOnly(2026, 11, 1)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("Plan inexistente", 1m, 100m, PlanId: 999999)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("VPS", 1.5m, 100m, PlanId: h.PlanId)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("Descuento", 1m, -100m)]));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => Create(h, [Migration], accountId: 999999));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run(h, s => s.Update(id, new QuoteInput(h.AccountId + 1, "Otra cuenta", [Migration]))));
        Assert.AreEqual(1, await h.Query(db => db.Quotes.CountAsync()));
        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Entity == $"quote:{id}")));
    }

    [TestMethod]
    public async Task Presupuesto_SeDuplicaEnBorrador_AunqueEsteCerrado_ConLaVigenciaDeHoy()
    {
        await using var h = await CoreHarness.Start();
        var id = await Create(h, [Migration, Disk], validUntil: new DateOnly(2026, 11, 10));
        await h.Use<QuoteService, ConvertedQuote>(s => s.Convert(id, QuoteTarget.Invoice));
        h.Clock.Now = h.Clock.Now.AddDays(20);

        var copyId = await h.Use<QuoteService, long>(s => s.Duplicate(id));

        var copy = await Get(h, copyId);
        Assert.AreNotEqual(id, copyId);
        Assert.AreEqual(QuoteStatus.Draft, copy.Status);
        CollectionAssert.AreEqual(new[] { Migration, Disk }, copy.Lines.ToArray());
        Assert.AreEqual(35000m, copy.Total);
        Assert.AreEqual(new DateOnly(2026, 12, 22), copy.ValidUntil, "La vigencia de siempre desde hoy, no la del original.");
        Assert.AreEqual("Incluye una hora de soporte.", copy.Notes);
        Assert.IsNull(copy.DocumentId);
        Assert.AreEqual(QuoteStatus.Invoiced, (await Get(h, id)).Status, "El original no cambia.");
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<QuoteService, long>(s => s.Duplicate(999999)));
    }

    [TestMethod]
    public async Task Presupuesto_Mandado_LoAvisa_YAceptadoSeConvierteEnFacturaUnaSolaVez()
    {
        await using var h = await CoreHarness.Start();
        var id = await Create(h, [Migration, Disk]);

        await Run(h, s => s.Send(id));

        var email = await h.Query(db => db.Set<OutboundEmail>().SingleAsync(e => e.Template == TemplateKeys.Quote));
        Assert.AreEqual("Presupuesto N.º " + id + ": Mudanza de servidor", email.Subject);
        StringAssert.Contains(email.Html, "Incluye una hora de soporte.");
        StringAssert.Contains(email.Html, $"https://billing.example.com/presupuestos/{id}");

        var accepted = await h.Use<QuoteService, ConvertedQuote>(s => s.Accept(id, [h.AccountId]));

        Assert.AreEqual(QuoteStatus.Invoiced, accepted.Status);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == accepted.DocumentId));
        Assert.IsTrue(invoice.IsIssued);
        Assert.AreEqual(35000m, invoice.Total);
        Assert.AreEqual(DocumentReasons.Manual, invoice.Reason);
        CollectionAssert.AreEqual(new[] { $"quote:{id}:0", $"quote:{id}:1" }, invoice.Lines.Select(l => l.DedupKey).ToArray());
        var quote = await Get(h, id);
        Assert.AreEqual(invoice.Id, quote.DocumentId);
        Assert.IsNotNull(quote.ClosedAt);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<QuoteService, ConvertedQuote>(s => s.Accept(id, [h.AccountId])));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<QuoteService, ConvertedQuote>(s => s.Convert(id, QuoteTarget.Invoice)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run(h, s => s.Update(id, new QuoteInput(h.AccountId, "Otro", [Migration]))));
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.Invoice)));
    }

    [TestMethod]
    public async Task Presupuesto_ConPlanes_PasaAPedidoConElPrecioPactado()
    {
        await using var h = await CoreHarness.Start();
        var id = await Create(h, [new QuoteLine("VPS 2 GB a precio de lanzamiento", 1m, 9000m, PlanId: h.PlanId), new QuoteLine("Instalación", 1m, 1500m)]);
        await Run(h, s => s.Send(id));

        var accepted = await h.Use<QuoteService, ConvertedQuote>(s => s.Accept(id, [h.AccountId]));

        Assert.AreEqual(QuoteStatus.Ordered, accepted.Status);
        var order = await h.Query(db => db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == accepted.OrderId));
        Assert.AreEqual($"quote:{id}", order.ClientReference);
        Assert.AreEqual(accepted.DocumentId, order.InvoiceId);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(9000m, service.UnitPrice, "El precio pactado reemplaza el del catálogo y sigue en las renovaciones.");
        Assert.AreEqual(0m, order.Lines.Single().SetupFee);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == accepted.DocumentId));
        CollectionAssert.AreEqual(new[] { LineKind.Renewal, LineKind.Manual }, invoice.Lines.Select(l => l.Kind).ToArray());
        Assert.AreEqual(10500m, invoice.Total);

        // Un presupuesto en otra moneda que la cuenta no pasa a pedido: el staff lo convierte en factura.
        var usd = await Create(h, [new QuoteLine("VPS", 1m, 10m, PlanId: h.PlanId)], currency: "USD");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<QuoteService, ConvertedQuote>(s => s.Convert(usd, QuoteTarget.Order)));
        var invoiced = await h.Use<QuoteService, ConvertedQuote>(s => s.Convert(usd, QuoteTarget.Invoice));
        Assert.AreEqual(Currency.Usd, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiced.DocumentId))).Currency);
        Assert.AreEqual(1, await h.Query(db => db.Services.CountAsync()));

        // El precio pactado reemplaza al del catálogo: un plan que solo tiene precio en dólares se vende en pesos igual.
        var dollarsOnly = await h.Query(async db =>
        {
            var plan = new Plan(await db.Plans.Where(p => p.Id == h.PlanId).Select(p => p.ProductId).SingleAsync(), BillingCycle.Annually);
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(plan.Id, Currency.Usd, 30m, 0m, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
            return plan.Id;
        });
        var quoted = await Create(h, [new QuoteLine("VPS anual", 1m, 40000m, PlanId: dollarsOnly)]);
        var ordered = await h.Use<QuoteService, ConvertedQuote>(s => s.Convert(quoted, QuoteTarget.Order));
        Assert.AreEqual(40000m, await h.Query(db => db.Services.Where(s => s.Id != service.Id).Select(s => s.UnitPrice).SingleAsync()));
        Assert.AreEqual(40000m, await h.Query(db => db.Documents.Where(d => d.Id == ordered.DocumentId).Select(d => d.Total).SingleAsync()));
    }

    [TestMethod]
    public async Task Presupuesto_ConUnPlanPorVariasUnidades_PasaAUnServicioPorUnidad_YElDeUnComplementoNoSeArma()
    {
        await using var h = await CoreHarness.Start();

        // El VPS no se pide por cantidad: en la tienda va de a una unidad por línea, y el renglón por dos pasa a dos servicios.
        var id = await Create(h, [new QuoteLine("VPS 2 GB", 2m, 9000m, PlanId: h.PlanId), new QuoteLine("Instalación", 1m, 1500m)]);
        await Run(h, s => s.Send(id));

        var accepted = await h.Use<QuoteService, ConvertedQuote>(s => s.Accept(id, [h.AccountId]));

        Assert.AreEqual(QuoteStatus.Ordered, accepted.Status);
        var services = await h.Query(db => db.Services.OrderBy(s => s.Id).ToListAsync());
        CollectionAssert.AreEqual(new[] { (1, 9000m), (1, 9000m) }, services.Select(s => (s.Quantity, s.UnitPrice)).ToArray(), "Cada unidad con el precio pactado.");
        Assert.AreEqual((await Get(h, id)).Total, await h.Query(db => db.Documents.Where(d => d.Id == accepted.DocumentId).Select(d => d.Total).SingleAsync()));

        // Un complemento cuelga de un servicio, y el pedido del presupuesto no tiene de cuál: con su plan no se cotiza.
        var (_, addon) = await h.Query(db => CatalogSeed.Product(db, "IP extra", 2500m, p => p.Addon = true));
        var error = await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("IP extra", 1m, 2500m, PlanId: addon)]));
        StringAssert.Contains(error.Message, "IP extra es un complemento");

        // Ni tantas unidades que la factura del pedido pase el tope de renglones, ni un precio que al desdoblarse cambie el total.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("VPS 2 GB", 201m, 9000m, PlanId: h.PlanId)]));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("VPS 2 GB", 2m, 9000.005m, PlanId: h.PlanId)]));
        Assert.AreEqual(1, await h.Query(db => db.Quotes.CountAsync()));
    }

    /// <summary>Planes de telefonía (27 %) y exento, con sus precios sin IVA (dólares): los renglones con plan llevan la categoría del producto.</summary>
    private static Task<(long Telecom, long Exempt)> OtherCategories(CoreHarness h) => h.Query(async db =>
    {
        db.TaxRules.Add(new TaxRule { Country = "AR", Category = TaxCategories.Telecom, Outcome = TaxTreatment.Taxed, Rate = 0.27m, ValidFrom = new DateOnly(2026, 1, 1) });
        var pbx = new Product { Name = "Central telefónica", Kind = ProductKind.Manual, TaxCategory = TaxCategories.Telecom };
        var domain = new Product { Name = "Dominio", Kind = ProductKind.Manual, TaxCategory = TaxCategories.Exempt };
        db.Products.AddRange(pbx, domain);
        await db.SaveChangesAsync();
        var monthly = new Plan(pbx.Id, BillingCycle.Monthly);
        var annual = new Plan(domain.Id, BillingCycle.Annually);
        db.Plans.AddRange(monthly, annual);
        await db.SaveChangesAsync();
        return (monthly.Id, annual.Id);
    });

    [TestMethod]
    public async Task Presupuesto_ConPlanesDeOtrasCategorias_YPreciosSinIva_ElTotalEsElDeLaFacturaDelPedido()
    {
        await using var h = await CoreHarness.Start();
        var (telecom, exempt) = await OtherCategories(h);

        // En dólares los precios van sin IVA: la categoría cambia el total, no solo el desglose. El renglón del plan
        // exento se carga como gravado a propósito: manda la categoría del producto, como en el pedido.
        var usd = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Cliente en dólares", "usd@example.com", Currency.Usd)));
        var lines = new QuoteLine[] { new("Central, abono mensual", 1m, 100m, PlanId: telecom), new("Dominio anual", 1m, 20m, PlanId: exempt), new("Instalación", 1m, 50m) };
        var id = await Create(h, lines, accountId: usd);
        var quote = await Get(h, id);
        Assert.AreEqual(207.50m, quote.Total, "100 al 27 %, 20 exentos y 50 al 21 %.");

        await Run(h, s => s.Send(id));
        var accepted = await h.Use<QuoteService, ConvertedQuote>(s => s.Accept(id, [usd]));

        Assert.AreEqual(QuoteStatus.Ordered, accepted.Status);
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == accepted.DocumentId));
        Assert.AreEqual(quote.Total, invoice.Total, "El cliente aceptó un importe y se le factura ese mismo importe.");
        Assert.AreEqual(PriceMode.TaxExcluded, invoice.PriceMode);
        CollectionAssert.AreEqual(new[] { (TaxCategories.Telecom, 0.27m), (TaxCategories.Exempt, 0m), (TaxCategories.Digital, 0.21m) }, invoice.Lines.Select(l => (l.Category, l.Rate)).ToArray());

        // Convertido en factura por el staff, el mismo total y las mismas categorías.
        var copy = await h.Use<QuoteService, long>(s => s.Duplicate(id));
        var invoiced = await h.Use<QuoteService, ConvertedQuote>(s => s.Convert(copy, QuoteTarget.Invoice));
        var manual = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == invoiced.DocumentId));
        Assert.AreEqual(207.50m, manual.Total);
        CollectionAssert.AreEqual(invoice.Lines.Select(l => (l.Category, l.Rate)).ToArray(), manual.Lines.Select(l => (l.Category, l.Rate)).ToArray());
    }

    [TestMethod]
    public async Task Presupuesto_ConTributosDeComprobante_ElTotalLosSuma()
    {
        await using var h = await CoreHarness.Start();
        await h.Use<TaxSetupService, bool>(async s =>
        {
            await s.ApplyPreset(new TaxPreset("AR", VatConditions.ResponsableInscripto, SellsAbroad: false, SellsToEuConsumers: false, SellsToUkConsumers: false, PerceivesRg2126: true, PaysGrossIncomeInCaba: false));
            return true;
        });
        var uncategorized = await h.Use<AccountService, long>(s => s.Create(new AccountInput("No categorizado", "nocat@example.com", Currency.Ars, Tax: new TaxIdentityInput(TaxDocumentType.Cuit, "30712345671", "Cliente SA", VatConditions.NoCategorizado, "AR", null))));

        var id = await Create(h, [new QuoteLine("Soporte", 1m, 12100m)], accountId: uncategorized);

        Assert.AreEqual(13370.50m, (await Get(h, id)).Total, "12.100 con IVA más la percepción de la RG 2126 sobre neto más IVA.");
        var invoiced = await h.Use<QuoteService, ConvertedQuote>(s => s.Convert(id, QuoteTarget.Invoice));
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoiced.DocumentId));
        Assert.AreEqual((13370.50m, 1270.50m), (invoice.Total, invoice.OtherTaxes));
    }

    [TestMethod]
    public async Task Presupuesto_ConPlanExentoYBonificacion_NoSeArma_YUnPedidoImposibleEsUnErrorDelPedido()
    {
        await using var h = await CoreHarness.Start();
        var (_, exempt) = await OtherCategories(h);

        // El plan sale exento y la bonificación gravada: la alícuota del 21 % sumaría negativo. Se frena al armarlo,
        // no al aceptarlo.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Create(h, [new QuoteLine("Dominio", 1m, 1000m, PlanId: exempt), new QuoteLine("Bonificación de bienvenida", 1m, -500m)]));
        Assert.AreEqual(0, await h.Query(db => db.Quotes.CountAsync()));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(exempt)], quoted: new QuotedTerms([1000m], [new LineInput("Bonificación", 1m, -500m)]))));
        Assert.AreEqual(0, await h.Query(db => db.Orders.CountAsync()));
    }

    [TestMethod]
    public async Task Presupuesto_ConPlanesEnOtraMoneda_ElClienteLoAceptaYPasaAFactura()
    {
        await using var h = await CoreHarness.Start();
        var id = await Create(h, [new QuoteLine("VPS", 1m, 10m, PlanId: h.PlanId)], currency: "USD");
        await Run(h, s => s.Send(id));

        var accepted = await h.Use<QuoteService, ConvertedQuote>(s => s.Accept(id, [h.AccountId]));

        Assert.AreEqual(QuoteStatus.Invoiced, accepted.Status);
        Assert.IsNull(accepted.OrderId);
        Assert.AreEqual(Currency.Usd, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == accepted.DocumentId))).Currency);
        Assert.AreEqual(0, await h.Query(db => db.Services.CountAsync()));
    }

    [TestMethod]
    public async Task Presupuesto_AjenoOEnBorradorNoSeVe_VencidoNoSeAcepta_YRechazadoSeCierra()
    {
        await using var h = await CoreHarness.Start();
        var draft = await Create(h, [Migration]);
        var sent = await Create(h, [Migration], validUntil: new DateOnly(2026, 11, 10));
        var declined = await Create(h, [Disk]);
        await Run(h, s => s.Send(sent));
        await Run(h, s => s.Send(declined));

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<QuoteService, ConvertedQuote>(s => s.Accept(draft, [h.AccountId])));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => h.Use<QuoteService, ConvertedQuote>(s => s.Accept(sent, [h.AccountId + 1])));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => Run(h, s => s.Decline(draft, [h.AccountId])));

        await Run(h, s => s.Decline(declined, [h.AccountId]));
        Assert.AreEqual(QuoteStatus.Declined, (await Get(h, declined)).Status);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run(h, s => s.Send(declined)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run(h, s => s.Delete(declined)));

        h.Clock.Now = new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
        Assert.IsTrue((await Get(h, sent)).IsExpired(new DateOnly(2026, 11, 11)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<QuoteService, ConvertedQuote>(s => s.Accept(sent, [h.AccountId])));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run(h, s => s.Send(sent)));
        var byStaff = await h.Use<QuoteService, ConvertedQuote>(s => s.Convert(sent, QuoteTarget.Invoice));
        Assert.AreEqual(QuoteStatus.Invoiced, byStaff.Status);

        await Run(h, s => s.Delete(draft));
        Assert.IsFalse(await h.Query(db => db.Quotes.AnyAsync(q => q.Id == draft)));
    }

    [TestMethod]
    public async Task Presupuesto_DosConversionesALaVez_UnaSolaFactura()
    {
        await using var h = await CoreHarness.Start();
        var id = await Create(h, [Migration]);
        using var other = h.Node();

        var results = await Task.WhenAll(
            Try(() => h.Use<QuoteService, ConvertedQuote>(s => s.Convert(id, QuoteTarget.Invoice))),
            Try(async () =>
            {
                await using var scope = other.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<QuoteService>().Convert(id, QuoteTarget.Invoice);
            }));

        Assert.AreEqual(1, results.Count(r => r is not null));
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.Invoice)));

        static async Task<ConvertedQuote?> Try(Func<Task<ConvertedQuote>> work)
        {
            try
            {
                return await work();
            }
            catch (ValidationException)
            {
                return null;
            }
        }
    }
}
