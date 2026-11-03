using System.Globalization;
using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Las pantallas de facturación avanzada de punta a punta: facturas manuales, presupuestos, ítems facturables, saldo a
/// favor, recargo por mora y cotizaciones en el admin, y presupuestos, pago de varias facturas y carga de saldo en el
/// portal.
/// </summary>
[TestClass]
[DoNotParallelize] // Como UiTests: cada prueba levanta su host y su base.
public sealed class BillingUiTests
{
    private static readonly string[] Billing = [StaffPermissions.AccountsRead, StaffPermissions.BillingRead, StaffPermissions.BillingWrite];

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static string S(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>El ID al final de una redirección como /admin/facturas/12.</summary>
    private static long Id(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var path = Location(response).Split('?', '#')[0];
        return long.Parse(path[(path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
    }

    private static async Task<string> Body(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>La pasarela falsa registrada en la web, con su host permitido para el navegador.</summary>
    private static async Task<TestModule> Gateway(UiHarness h, FakeGateway gateway)
    {
        var module = await TestModule.Start(gateway.Definition(), work => h.Use<ModuleRegistry, long>(work));
        await h.Query(async db =>
        {
            (await db.ModuleInstances.SingleAsync()).BrowserHosts = """["pay.example.com"]""";
            return await db.SaveChangesAsync();
        });
        return module;
    }

    [TestMethod]
    public async Task FacturaManual_SeGuardaEnBorrador_SeEmiteYQuedaPorPagar()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("facturacion@example.com", Billing);
        using var c = await UiTests.StaffSession(h, "facturacion@example.com");

        var menu = await c.Html("/admin");
        foreach (var href in new[] { "/admin/facturas", "/admin/presupuestos", "/admin/items", "/admin/facturas?tipo=nota-de-credito", "/admin/monedas" })
        {
            StringAssert.Contains(menu, $"href=\"{href}\"", href);
        }

        var form = $"/admin/facturas/nueva?cuenta={h.AccountA}";
        Dictionary<string, string> Lines(string price) => new()
        {
            ["cuenta"] = S(h.AccountA),
            ["Currency"] = "ARS",
            ["Lines[0].Description"] = "Horas de soporte",
            ["Lines[0].Quantity"] = "2",
            ["Lines[0].UnitPrice"] = price,
            ["Lines[0].Treatment"] = "Taxed",
            ["Lines[1].Description"] = string.Empty,
            ["Lines[1].Quantity"] = "1",
            ["Lines[1].UnitPrice"] = string.Empty,
        };

        using (var bad = await c.Submit(form, "/admin/facturas/nueva?handler=Draft", Lines("seis mil")))
        {
            var html = await Body(bad);
            StringAssert.Contains(html, "no es un número");
            StringAssert.Contains(html, "value=\"Horas de soporte\"", "El formulario vuelve con lo cargado.");
        }

        long id;
        using (var draft = await c.Submit(form, "/admin/facturas/nueva?handler=Draft", Lines("6050")))
        {
            id = Id(draft);
        }

        var page = await c.Html($"/admin/facturas/{id}");
        StringAssert.Contains(page, "Horas de soporte");
        StringAssert.Contains(page, "handler=Issue");
        Assert.IsNull((await h.Query(db => db.Documents.SingleAsync(d => d.Id == id))).Number, "El borrador no consume número.");
        StringAssert.Contains(await c.Html("/admin/facturas?estado=borradores"), $"/admin/facturas/{id}\"");

        using (var issue = await c.Submit($"/admin/facturas/{id}", $"/admin/facturas/{id}?handler=Issue", []))
        {
            Assert.AreEqual($"/admin/facturas/{id}", Location(issue));
        }

        var doc = await h.Query(db => db.Documents.SingleAsync(d => d.Id == id));
        Assert.IsTrue(doc.IsIssued);
        Assert.IsNotNull(doc.Number);
        Assert.AreEqual(12100m, doc.Total);
        Assert.AreEqual(2100m, doc.Tax);
        StringAssert.Contains(await c.Html($"/admin/facturas?estado=por-pagar&cuenta={h.AccountA}"), $"/admin/facturas/{id}\"");
        Assert.IsFalse((await c.Html($"/admin/facturas?cuenta={h.AccountB}")).Contains($"/admin/facturas/{id}\"", StringComparison.Ordinal));
        Assert.IsFalse((await c.Html($"/admin/facturas?tipo=nota-de-credito&cuenta={h.AccountA}")).Contains($"/admin/facturas/{id}\"", StringComparison.Ordinal));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "invoice.issue" && a.Entity == $"document:{id}")));
    }

    [TestMethod]
    public async Task SinPermisos_NoVeLaFacturacion_YElLectorNoCambiaNada()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("soporte@example.com", StaffPermissions.AccountsRead);
        await h.Staff("lector@example.com", StaffPermissions.AccountsRead, StaffPermissions.BillingRead);

        using (var support = await UiTests.StaffSession(h, "soporte@example.com"))
        {
            Assert.IsFalse((await support.Html("/admin")).Contains("href=\"/admin/presupuestos\"", StringComparison.Ordinal));
            foreach (var path in new[] { "/admin/facturas", $"/admin/facturas/{h.InvoiceA}", "/admin/presupuestos", "/admin/items", "/admin/monedas" })
            {
                using var response = await support.Get(path);
                StringAssert.Contains(Location(response), "/estado/403", path);
            }
        }

        using var reader = await UiTests.StaffSession(h, "lector@example.com");
        var invoice = await reader.Html($"/admin/facturas/{h.InvoiceA}");
        StringAssert.Contains(invoice, "12.100,00 ARS");
        Assert.IsFalse(invoice.Contains("handler=", StringComparison.Ordinal), "El lector no ve las acciones.");
        using (var create = await reader.Get($"/admin/facturas/nueva?cuenta={h.AccountA}"))
        {
            StringAssert.Contains(Location(create), "/estado/403");
        }

        using (var quote = await reader.Get($"/admin/presupuestos/nuevo?cuenta={h.AccountA}"))
        {
            StringAssert.Contains(Location(quote), "/estado/403");
        }

        // Un formulario armado a mano tampoco pasa: el permiso lo mira cada acción.
        using (var fee = await reader.Submit($"/admin/facturas/{h.InvoiceA}", $"/admin/facturas/{h.InvoiceA}?handler=LateFee", []))
        {
            StringAssert.Contains(Location(fee), "/estado/403");
        }

        var customer = $"/admin/cuentas/{h.AccountA}";
        using (var adjust = await reader.Submit(customer, $"{customer}?handler=Adjust", new() { ["AdjustCurrency"] = "ARS", ["AdjustAmount"] = "100", ["AdjustReason"] = "Prueba" }))
        {
            StringAssert.Contains(Location(adjust), "/estado/403");
        }

        using (var exempt = await reader.Submit(customer, $"{customer}?handler=Exemptions", new() { ["NoLateFees"] = "true" }))
        {
            StringAssert.Contains(Location(exempt), "/estado/403");
        }

        Assert.AreEqual(0, await h.Query(db => db.CreditEntries.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.DebitNote)));
        Assert.IsFalse((await h.Query(db => db.Accounts.SingleAsync(a => a.Id == h.AccountA))).NoLateFees);
    }

    [TestMethod]
    public async Task Presupuesto_ElStaffLoArmaYLoManda_ElClienteLoAceptaYQuedaListoParaPagar()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("ventas@example.com", Billing);
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.PortalUser("ajena@example.com", h.AccountB);
        using var staff = await UiTests.StaffSession(h, "ventas@example.com");

        long id;
        using (var save = await staff.Submit($"/admin/presupuestos/nuevo?cuenta={h.AccountA}", "/admin/presupuestos/nuevo?handler=Save", new()
        {
            ["cuenta"] = S(h.AccountA),
            ["Subject"] = "VPS con migración",
            ["Currency"] = "ARS",
            ["Lines[0].Description"] = "VPS 2 GB a precio pactado",
            ["Lines[0].PlanId"] = S(h.PlanId),
            ["Lines[0].Quantity"] = "1",
            ["Lines[0].UnitPrice"] = "10000",
            ["Lines[0].Treatment"] = "Taxed",
            ["Lines[1].Description"] = "Migración del sitio",
            ["Lines[1].PlanId"] = string.Empty,
            ["Lines[1].Quantity"] = "1",
            ["Lines[1].UnitPrice"] = "5000",
            ["Lines[1].Treatment"] = "Taxed",
            ["Notes"] = "Incluye la migración de un sitio.",
        }))
        {
            id = Id(save);
        }

        using var client = h.NewClient();
        (await client.Login("titular@example.com")).Dispose();
        using (var hidden = await client.Get($"/presupuestos/{id}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode, "Un borrador no se ve en el portal.");
        }

        using (var send = await staff.Submit($"/admin/presupuestos/{id}", $"/admin/presupuestos/{id}?handler=Send", []))
        {
            Assert.AreEqual($"/admin/presupuestos/{id}", Location(send));
        }

        Assert.AreEqual(1, await h.Query(db => db.Set<OutboundEmail>().CountAsync(e => e.Template == TemplateKeys.Quote && e.AccountId == h.AccountA)));
        StringAssert.Contains(await client.Html("/presupuestos"), $"/presupuestos/{id}\"");
        var page = await client.Html($"/presupuestos/{id}");
        StringAssert.Contains(page, "VPS con migración");
        StringAssert.Contains(page, "15.000,00 ARS");
        StringAssert.Contains(page, "handler=Accept");

        using (var other = h.NewClient())
        {
            (await other.Login("ajena@example.com")).Dispose();
            using var foreign = await other.Get($"/presupuestos/{id}");
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.IsFalse((await other.Html("/presupuestos")).Contains($"/presupuestos/{id}\"", StringComparison.Ordinal));
        }

        long invoice;
        using (var accept = await client.Submit($"/presupuestos/{id}", $"/presupuestos/{id}?handler=Accept", []))
        {
            StringAssert.StartsWith(Location(accept), "/pagar?factura=");
            invoice = long.Parse(Location(accept)["/pagar?factura=".Length..], CultureInfo.InvariantCulture);
        }

        var quote = await h.Query(db => db.Quotes.SingleAsync(q => q.Id == id));
        Assert.AreEqual(QuoteStatus.Ordered, quote.Status);
        Assert.AreEqual(invoice, quote.DocumentId);
        Assert.AreEqual(1, await h.Query(db => db.Services.CountAsync(s => s.AccountId == h.AccountA && s.UnitPrice == 10000m)), "El servicio sale con el precio pactado.");
        Assert.AreEqual(15000m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == invoice))).Total);
        StringAssert.Contains(await client.Html($"/pagar?factura={invoice}"), "15.000,00 ARS");

        // Aceptado no se acepta de nuevo, aunque el formulario llegue igual.
        using (var again = await client.Submit($"/presupuestos/{id}", $"/presupuestos/{id}?handler=Accept", []))
        {
            StringAssert.Contains(await Body(again), "ya no se puede aceptar");
        }

        Assert.AreEqual(1, await h.Query(db => db.Orders.CountAsync(o => o.AccountId == h.AccountA && o.ClientReference == $"quote:{id}")));
        StringAssert.Contains(await staff.Html("/admin/presupuestos?estado=aceptados"), $"/admin/presupuestos/{id}\"");
        StringAssert.Contains(await staff.Html($"/admin/cuentas/{h.AccountA}/presupuestos?estado=todos"), $"/admin/presupuestos/{id}\"");
        Assert.IsFalse((await staff.Html($"/admin/cuentas/{h.AccountB}/presupuestos?estado=todos")).Contains($"/admin/presupuestos/{id}\"", StringComparison.Ordinal));

        // Uno cerrado se duplica en un borrador nuevo, que aparece entre los vigentes y no entre los vencidos.
        long copy;
        using (var duplicate = await staff.Submit($"/admin/presupuestos/{id}", $"/admin/presupuestos/{id}?handler=Duplicate", []))
        {
            copy = Id(duplicate);
        }

        Assert.AreNotEqual(id, copy);
        Assert.AreEqual(QuoteStatus.Draft, (await h.Query(db => db.Quotes.SingleAsync(q => q.Id == copy))).Status);
        StringAssert.Contains(await staff.Html("/admin/presupuestos?estado=vigentes"), $"/admin/presupuestos/{copy}\"");
        Assert.IsFalse((await staff.Html("/admin/presupuestos?estado=vencidos")).Contains($"/admin/presupuestos/{copy}\"", StringComparison.Ordinal));
        Assert.IsFalse((await staff.Html("/admin/presupuestos?estado=vigentes")).Contains($"/admin/presupuestos/{id}\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PresupuestoEnDolares_MuestraElEquivalenteConLaCotizacionCargada_YElClienteLoRechaza()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("ventas@example.com", Billing);
        await h.PortalUser("titular@example.com", h.AccountA);
        using var staff = await UiTests.StaffSession(h, "ventas@example.com");

        StringAssert.Contains(await staff.Html("/admin/monedas"), "Sin cotización vigente");
        using (var rate = await staff.Submit("/admin/monedas", "/admin/monedas?handler=Manual", new() { ["Currency"] = "USD", ["Rate"] = "1465.5", ["Date"] = string.Empty }))
        {
            Assert.AreEqual("/admin/monedas", Location(rate));
        }

        var rates = await staff.Html("/admin/monedas");
        StringAssert.Contains(rates, "1.465,50 ARS");
        StringAssert.Contains(rates, "cargada por el staff");
        using (var refresh = await staff.Submit("/admin/monedas", "/admin/monedas?handler=Refresh", []))
        {
            Assert.AreEqual("/admin/monedas?pedido=true", Location(refresh));
        }

        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ExchangeRates)), "El pedido al banco es un trabajo: la web no sale a la red.");

        long id;
        using (var save = await staff.Submit($"/admin/presupuestos/nuevo?cuenta={h.AccountA}", "/admin/presupuestos/nuevo?handler=Save", new()
        {
            ["cuenta"] = S(h.AccountA),
            ["Subject"] = "Consultoría",
            ["Currency"] = "USD",
            ["Lines[0].Description"] = "Horas de consultoría",
            ["Lines[0].Quantity"] = "4",
            ["Lines[0].UnitPrice"] = "25",
            ["Lines[0].Treatment"] = "Taxed",
        }))
        {
            id = Id(save);
        }

        (await staff.Submit($"/admin/presupuestos/{id}", $"/admin/presupuestos/{id}?handler=Send", [])).Dispose();
        using var client = h.NewClient();
        (await client.Login("titular@example.com")).Dispose();
        var page = await client.Html($"/presupuestos/{id}");
        StringAssert.Contains(page, "100,00 USD");
        StringAssert.Contains(page, "Equivale a");
        StringAssert.Contains(page, "177.325,50 ARS", "121 dólares con IVA, porque en dólares los precios se cargan sin él.");
        var admin = await staff.Html($"/admin/presupuestos/{id}");
        StringAssert.Contains(admin, "handler=Invoice");
        Assert.IsFalse(admin.Contains("handler=Order", StringComparison.Ordinal), "Sin planes no se convierte en pedido.");

        using (var decline = await client.Submit($"/presupuestos/{id}", $"/presupuestos/{id}?handler=Decline", []))
        {
            Assert.AreEqual($"/presupuestos/{id}", Location(decline));
        }

        Assert.AreEqual(QuoteStatus.Declined, (await h.Query(db => db.Quotes.SingleAsync(q => q.Id == id))).Status);
        Assert.IsFalse((await client.Html($"/presupuestos/{id}")).Contains("handler=Accept", StringComparison.Ordinal));
        StringAssert.Contains(await staff.Html("/admin/presupuestos?estado=rechazados"), $"/admin/presupuestos/{id}\"");
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Currency == Domain.Shared.Currency.Usd)), "Rechazado no factura nada.");
    }

    [TestMethod]
    public async Task ItemsFacturables_SeCarganSeCancelanYSeFacturanEnElMomento()
    {
        await using var h = await UiHarness.Start();
        await h.Staff("facturacion@example.com", Billing);
        using var c = await UiTests.StaffSession(h, "facturacion@example.com");
        var page = $"/admin/items?cuenta={h.AccountA}";
        Dictionary<string, string> Item(string description, string price) => new()
        {
            ["cuenta"] = S(h.AccountA),
            ["Description"] = description,
            ["Quantity"] = "1",
            ["UnitPrice"] = price,
            ["Treatment"] = "Taxed",
            ["Currency"] = string.Empty,
            ["InvoiceOn"] = string.Empty,
        };

        foreach (var (description, price) in new[] { ("Disco de reemplazo", "24200"), ("Horas de soporte", "6050") })
        {
            using var add = await c.Submit(page, "/admin/items?handler=Add", Item(description, price));
            Assert.AreEqual(page, Location(add));
        }

        using (var bad = await c.Submit(page, "/admin/items?handler=Add", Item("Sin precio", "gratis")))
        {
            StringAssert.Contains(await Body(bad), "no es un número");
        }

        var items = await h.Query(db => db.BillableItems.OrderBy(i => i.Id).ToListAsync());
        Assert.HasCount(2, items);
        StringAssert.Contains(await c.Html(page), "Horas de soporte");
        using (var cancel = await c.Submit(page, "/admin/items?handler=Cancel", new() { ["item"] = S(items[1].Id), ["cuenta"] = S(h.AccountA) }))
        {
            Assert.AreEqual(page, Location(cancel));
        }

        using (var invoice = await c.Submit(page, "/admin/items?handler=Invoice", new() { ["cuenta"] = S(h.AccountA) }))
        {
            Assert.AreEqual(page, Location(invoice));
        }

        items = await h.Query(db => db.BillableItems.OrderBy(i => i.Id).ToListAsync());
        Assert.AreEqual(BillableItemStatus.Invoiced, items[0].Status);
        Assert.AreEqual(BillableItemStatus.Cancelled, items[1].Status);
        var doc = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == items[0].DocumentId));
        Assert.IsTrue(doc.IsIssued);
        Assert.AreEqual(24200m, doc.Total);
        Assert.AreEqual("Disco de reemplazo", doc.Lines.Single().Description);
        StringAssert.Contains(await c.Html($"{page}&estado=Invoiced"), $"/admin/facturas/{doc.Id}\"");

        // Sin pendientes no hay nada que facturar, y uno facturado ya no se cancela.
        using (var nothing = await c.Submit(page, "/admin/items?handler=Invoice", new() { ["cuenta"] = S(h.AccountA) }))
        {
            StringAssert.Contains(await Body(nothing), "no tiene ítems pendientes");
        }

        using (var late = await c.Submit(page, "/admin/items?handler=Cancel", new() { ["item"] = S(items[0].Id), ["cuenta"] = S(h.AccountA) }))
        {
            StringAssert.Contains(await Body(late), "nota de crédito");
        }
    }

    [TestMethod]
    public async Task SaldoAFavor_ElAjusteQuedaEnElLibroYLaAuditoria_PagaLaFactura_YElRecargoRespetaLaExencion()
    {
        await using var h = await UiHarness.Start(settings: new Dictionary<string, string?> { ["Billing:LateFeePercent"] = "10" });
        var staff = await h.Staff("cobranzas@example.com", [.. Billing, StaffPermissions.CreditAdjust]);
        using var c = await UiTests.StaffSession(h, "cobranzas@example.com");
        var customer = $"/admin/cuentas/{h.AccountA}";
        Dictionary<string, string> Adjust(string amount, string reason) => new() { ["AdjustCurrency"] = "ARS", ["AdjustAmount"] = amount, ["AdjustReason"] = reason };

        using (var add = await c.Submit(customer, $"{customer}?handler=Adjust", Adjust("2000", "Compensación por un corte")))
        {
            Assert.AreEqual($"{customer}#saldo", Location(add));
        }

        using (var tooMuch = await c.Submit(customer, $"{customer}?handler=Adjust", Adjust("-5000", "Error de carga")))
        {
            StringAssert.Contains(await Body(tooMuch), "no alcanza");
        }

        using (var noReason = await c.Submit(customer, $"{customer}?handler=Adjust", Adjust("100", " ")))
        {
            StringAssert.Contains(await Body(noReason), "necesita un motivo");
        }

        var page = await c.Html(customer);
        StringAssert.Contains(page, "Compensación por un corte");
        StringAssert.Contains(page, "2.000,00 ARS");
        var entry = await h.Query(db => db.CreditEntries.SingleAsync());
        Assert.AreEqual(2000m, entry.Amount);
        Assert.AreEqual($"staff:{staff.Id}", entry.Actor);
        var audit = await h.Query(db => db.Set<AuditEvent>().SingleAsync(a => a.Action == "credit.adjust"));
        Assert.AreEqual($"staff:{staff.Id}", audit.Actor);
        Assert.AreEqual($"account:{h.AccountA}", audit.Entity);
        Assert.IsFalse(audit.Details?.Contains("Compensación", StringComparison.Ordinal) ?? false, "El motivo, que puede tener datos personales, no va a la auditoría.");

        var invoice = $"/admin/facturas/{h.InvoiceA}";
        StringAssert.Contains(await c.Html(invoice), "Pagar con el saldo a favor (2.000,00 ARS)");
        using (var pay = await c.Submit(invoice, $"{invoice}?handler=Credit", []))
        {
            Assert.AreEqual(invoice, Location(pay));
        }

        Assert.AreEqual(10100m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == h.InvoiceA))).Pending);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == h.AccountA).SumAsync(b => b.Balance)));

        // El recargo se ofrece recién con la factura vencida.
        Assert.IsFalse((await c.Html(invoice)).Contains("handler=LateFee", StringComparison.Ordinal));
        await h.Query(db => TestDatabase.MakeOverdue(db, h.InvoiceA, 30));
        StringAssert.Contains(await c.Html(invoice), "handler=LateFee");

        // Exenta, el recargo no corresponde aunque el staff lo pida.
        using (var exempt = await c.Submit(customer, $"{customer}?handler=Exemptions", new() { ["NoLateFees"] = "true" }))
        {
            Assert.AreEqual($"{customer}#facturacion", Location(exempt));
        }

        var account = await h.Query(db => db.Accounts.SingleAsync(a => a.Id == h.AccountA));
        Assert.IsTrue(account.NoLateFees);
        Assert.IsFalse(account.NoReminders);
        using (var refused = await c.Submit(invoice, $"{invoice}?handler=LateFee", []))
        {
            StringAssert.Contains(await Body(refused), "No corresponde un recargo");
        }

        (await c.Submit(customer, $"{customer}?handler=Exemptions", [])).Dispose();
        Assert.IsFalse((await h.Query(db => db.Accounts.SingleAsync(a => a.Id == h.AccountA))).NoLateFees);
        using (var fee = await c.Submit(invoice, $"{invoice}?handler=LateFee", []))
        {
            Assert.AreEqual(invoice, Location(fee));
        }

        var note = await h.Query(db => db.Documents.SingleAsync(d => d.RelatedDocumentId == h.InvoiceA && d.Kind == DocumentKind.DebitNote));
        Assert.AreEqual(1010m, note.Total, "El 10 % de lo que se debe.");
        StringAssert.Contains(await c.Html(invoice), $"/admin/facturas/{note.Id}\"");
        using (var twice = await c.Submit(invoice, $"{invoice}?handler=LateFee", []))
        {
            StringAssert.Contains(await Body(twice), "No corresponde un recargo");
        }

        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "account.billing-exemptions" && a.Actor == $"staff:{staff.Id}")));
    }

    [TestMethod]
    public async Task Portal_PagaVariasFacturasConUnSoloIntento_YUsaElSaldoAFavorAntes()
    {
        await using var h = await UiHarness.Start();
        await h.PortalUser("titular@example.com", h.AccountA);
        var gateway = new FakeGateway();
        await using var module = await Gateway(h, gateway);
        var second = await h.Use<InvoiceService, long>(s => s.CreateManual(new ManualInvoiceInput(h.AccountA, [new LineInput("Disco de reemplazo", 1m, 2420m)])));

        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        var home = await c.Html("/");
        StringAssert.Contains(home, "href=\"/presupuestos\"");
        Assert.IsFalse(home.Contains("href=\"/cargar-saldo\"", StringComparison.Ordinal), "Sin límites configurados no se ofrece la carga.");
        using (var noTopUp = await c.Get("/cargar-saldo"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, noTopUp.StatusCode);
        }

        StringAssert.Contains(await c.Html("/facturas"), $"/pagar?moneda=ARS&amp;cuenta={h.AccountA}\"", "Pagar todo lo de la cuenta en una moneda.");
        var pay = await c.Html("/pagar");
        StringAssert.Contains(pay, "14.520,00 ARS");
        StringAssert.Contains(pay, $"/facturas/{h.InvoiceA}\"");
        StringAssert.Contains(pay, $"/facturas/{second}\"");
        using (var foreign = await c.Get($"/pagar?factura={h.InvoiceA}&factura={h.InvoiceB}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode, "Una factura ajena da lo mismo que una que no existe.");
        }

        var both = $"factura={h.InvoiceA}&factura={second}";
        using (var start = await c.Submit("/pagar", $"/pagar?{both}", new() { ["method"] = $"g{module.InstanceId}" }))
        {
            StringAssert.StartsWith(Location(start), "https://pay.example.com/checkout?ref=");
        }

        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(14520m, attempt.Amount);
        CollectionAssert.AreEquivalent(new[] { h.InvoiceA, second }, attempt.DocumentIds.ToArray());
        Assert.AreEqual("14520.00", gateway.Checkouts.Single().Input["amount"]!["value"]!.GetValue<string>());

        // El saldo a favor paga primero, de la que vence antes a la que vence después. Lo que falta se paga con un medio.
        await h.Use<InvoiceService, long>(s => s.AdjustCredit(h.AccountA, "ARS", 3000m, "Bonificación"));
        StringAssert.Contains(await c.Html($"/pagar?{both}"), "3.000,00 ARS");
        using (var credit = await c.Submit($"/pagar?{both}", $"/pagar?handler=Credit&{both}", []))
        {
            StringAssert.StartsWith(Location(credit), "/pagar?factura=");
        }

        var pending = await h.Query(db => db.Documents.Where(d => d.Id == h.InvoiceA || d.Id == second).SumAsync(d => d.Total - d.Credited - d.Allocated));
        Assert.AreEqual(11520m, pending);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == h.AccountA).SumAsync(b => b.Balance)));
    }

    [TestMethod]
    public async Task Portal_CargaSaldo_DentroDeLosLimites_ConLaPasarelaQueCobraEnEsaMoneda()
    {
        await using var h = await UiHarness.Start(settings: new Dictionary<string, string?> { ["Billing:TopUp:ARS:Min"] = "1000", ["Billing:TopUp:ARS:Max"] = "50000" });
        await h.PortalUser("titular@example.com", h.AccountA);
        var gateway = new FakeGateway { Currencies = ["ARS"] };
        await using var module = await Gateway(h, gateway);

        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        StringAssert.Contains(await c.Html("/"), "href=\"/cargar-saldo\"");
        var page = await c.Html("/cargar-saldo");
        StringAssert.Contains(page, "1.000,00 ARS");
        StringAssert.Contains(page, "50.000,00 ARS");
        StringAssert.Contains(page, $"value=\"{module.InstanceId}\"");
        Dictionary<string, string> TopUp(string amount) => new() { ["cuenta"] = S(h.AccountA), ["moneda"] = "ARS", ["Amount"] = amount, ["Instance"] = S(module.InstanceId) };

        using (var low = await c.Submit("/cargar-saldo", "/cargar-saldo", TopUp("999")))
        {
            StringAssert.Contains(await Body(low), "La carga en ARS va de 1000.00 a 50000.00");
        }

        using (var ok = await c.Submit("/cargar-saldo", "/cargar-saldo", TopUp("5000")))
        {
            StringAssert.StartsWith(Location(ok), "https://pay.example.com/checkout?ref=");
        }

        var attempt = await h.Query(db => db.PaymentAttempts.SingleAsync());
        Assert.AreEqual(5000m, attempt.Amount);
        Assert.AreEqual(h.AccountA, attempt.AccountId);
        Assert.IsEmpty(attempt.DocumentIds);
        using var foreign = await c.Get($"/cargar-saldo?cuenta={h.AccountB}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }
}
