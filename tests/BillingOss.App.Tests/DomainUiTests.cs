using System.Globalization;
using System.Net;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Las pantallas de dominios del portal y del admin, de punta a punta contra el registrar falso.</summary>
[TestClass]
[DoNotParallelize] // Cada prueba levanta su host y su base, como UiTests.
public sealed class DomainUiTests
{
    private sealed record Setup(UiHarness H, TestModule Module, FakeRegistrarModule Fake) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Module.DisposeAsync();
            await H.DisposeAsync();
        }
    }

    private static async Task<Setup> Start()
    {
        var h = await UiHarness.Start();
        var fake = new FakeRegistrarModule();
        var module = await TestModule.Start(fake.Definition(), work => h.Use<ModuleRegistry, long>(work));
        await h.Db(db => FakeRegistrarModule.Seed(db, module.InstanceId, h.AccountA));
        return new Setup(h, module, fake);
    }

    /// <summary>Un dominio activo de la cuenta, que el registrar falso también tiene.</summary>
    private static async Task<long> Active(Setup s, long accountId, string name)
    {
        s.Fake.Domains[name] = new FakeRegistrarModule.FakeEntry { Expires = new DateOnly(2027, 6, 1), Nameservers = ["ns1.viejo.net", "ns2.viejo.net"] };
        long id = 0;
        await s.H.Db(async db =>
        {
            var domain = new RegisteredDomain(accountId, name, s.Module.InstanceId, Currency.Ars, 16000m, DateTimeOffset.UtcNow) { Nameservers = """["ns1.viejo.net","ns2.viejo.net"]""" };
            domain.Start(new DateOnly(2027, 6, 1));
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            id = domain.Id;
        });
        return id;
    }

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? string.Empty;

    private static async Task<string> Body(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [TestMethod]
    public async Task Portal_BuscarElegirLosAñosYPedir_LlevaAPagarLaFactura_YLoAjenoDa404()
    {
        await using var s = await Start();
        var h = s.H;
        await h.PortalUser("titular@example.com", h.AccountA);
        var foreign = await Active(s, h.AccountB, "ajeno.com");
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        StringAssert.Contains(await c.Html("/dominios"), "Todavía no tenés dominios");
        var search = await c.Html("/dominios/nuevo?q=MiEmpresa");
        var confirm = await c.Html("/dominios/nuevo?nombre=miempresa.com");
        using var placed = await c.Submit("/dominios/nuevo?nombre=miempresa.com", "/dominios/nuevo", new()
        {
            ["nombre"] = "miempresa.com", ["transferir"] = "false", ["cuenta"] = h.AccountA.ToString(CultureInfo.InvariantCulture), ["Years"] = "2", ["Privacy"] = "true",
        });

        StringAssert.Contains(search, "miempresa.com");
        StringAssert.Contains(search, "15.000,00");
        StringAssert.Contains(search, "/dominios/nuevo?nombre=miempresa.com");
        s.Fake.Premium.Add("caro.com");
        var premium = await c.Html("/dominios/nuevo?q=caro.com");
        StringAssert.Contains(premium, "Premium");
        Assert.IsFalse(premium.Contains("nombre=caro.com", StringComparison.Ordinal), "Un premium no se pide al precio del TLD.");
        StringAssert.Contains(confirm, "29.000,00");
        StringAssert.Contains(confirm, "Cliente de prueba");
        StringAssert.Contains(confirm, "Ocultar los datos del titular");
        Assert.AreEqual(HttpStatusCode.Redirect, placed.StatusCode);
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Name == "miempresa.com"));
        Assert.AreEqual((2, DomainStatus.Pending), (domain.Years, domain.Status));
        var input = await h.Query(db => db.DomainOperations.Where(o => o.DomainId == domain.Id).Select(o => o.Input).SingleAsync());
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.Parse(input)!["privacy"]!.GetValue<bool>(), "La protección va con el registro.");
        var invoice = await h.Query(db => db.DocumentLines.Where(l => l.DomainId == domain.Id).Select(l => l.DocumentId).SingleAsync());
        Assert.AreEqual($"/facturas/{invoice}", Location(placed));
        StringAssert.Contains(await c.Html("/dominios"), "Pedido en curso");
        StringAssert.Contains(await c.Html($"/dominios/{domain.Id}"), "apenas se pague la factura");
        using var other = await c.Get($"/dominios/{foreign}");
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        Assert.IsFalse((await c.Html("/dominios")).Contains("ajeno.com", StringComparison.Ordinal));

        // Una extensión que no ofrece la protección no la muestra al pedir.
        await h.Db(async db =>
        {
            db.TldOptions.Add(new TldOptions(".com") { Privacy = false });
            await db.SaveChangesAsync();
        });
        Assert.IsFalse((await c.Html("/dominios/nuevo?nombre=otra.com")).Contains("Ocultar los datos del titular", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Portal_NameserversBloqueoNoRenovar_YElCodigoEppConConfirmacionYAuditoria()
    {
        await using var s = await Start();
        var h = s.H;
        await h.PortalUser("titular@example.com", h.AccountA);
        var id = await Active(s, h.AccountA, "ejemplo.com");
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        var page = $"/dominios/{id}";

        StringAssert.Contains(await c.Html(page), "ns1.viejo.net");
        var badNs = await Body(await c.Submit(page, $"{page}?handler=Nameservers", new() { ["Nameservers[0]"] = "ns1.otro.net", ["Nameservers[1]"] = "" }));
        var okNs = await Body(await c.Submit(page, $"{page}?handler=Nameservers", new() { ["Nameservers[0]"] = "ns1.otro.net", ["Nameservers[1]"] = "ns2.otro.net", ["Nameservers[2]"] = "" }));
        var locked = await Body(await c.Submit(page, $"{page}?handler=Lock", new() { ["locked"] = "true" }));
        var code = await Body(await c.Submit(page, $"{page}?handler=AuthCode", []));
        await h.RunJobs();
        var unconfirmed = await Body(await c.Submit(page, $"{page}?handler=Reveal", []));
        var revealed = await Body(await c.Submit(page, $"{page}?handler=Reveal", new() { ["confirm"] = "true" }));
        var noRenew = await Body(await c.Submit(page, $"{page}?handler=AutoRenew", new() { ["on"] = "false" }));

        StringAssert.Contains(badNs, "entre 2 y 13 nameservers");
        StringAssert.Contains(okNs, "Mandamos los nameservers nuevos");
        StringAssert.Contains(locked, "Pedimos el bloqueo");
        StringAssert.Contains(code, "Pedimos el código al registrar");
        StringAssert.Contains(unconfirmed, "marcá la casilla");
        Assert.IsFalse(unconfirmed.Contains("EPP-DEL-REGISTRAR", StringComparison.Ordinal));
        StringAssert.Contains(revealed, "EPP-DEL-REGISTRAR");
        StringAssert.Contains(noRenew, "no se va a renovar");
        CollectionAssert.AreEqual(new[] { "ns1.otro.net", "ns2.otro.net" }, s.Fake.Domains["ejemplo.com"].Nameservers);
        Assert.IsTrue(s.Fake.Domains["ejemplo.com"].Locked);
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == id));
        Assert.AreEqual((false, true), (domain.AutoRenew, domain.Locked));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "domain.auth_code.reveal" && a.Entity == $"domain:{id}")));
        StringAssert.Contains(await c.Html("/"), "No hay dominios por vencer");
    }

    [TestMethod]
    public async Task Portal_DnsYCorreoEnUnaTabla_LaProteccionDesdeElDominio_YLoAjenoDa404()
    {
        await using var s = await Start();
        var h = s.H;
        await h.PortalUser("titular@example.com", h.AccountA);
        var id = await Active(s, h.AccountA, "ejemplo.com");
        var foreign = await Active(s, h.AccountB, "ajeno.com");
        s.Fake.Domains["ejemplo.com"].Records = [new("@", "A", "198.51.100.1"), new("www", "CNAME", "ejemplo.com")];
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();
        var page = $"/dominios/{id}/dns";

        var domainPage = await c.Html($"/dominios/{id}");
        var view = await c.Html(page);
        var bad = await Body(await c.Submit(page, $"{page}?handler=Zone", new()
        {
            ["Records[0].Name"] = "@", ["Records[0].Type"] = "A", ["Records[0].Value"] = "198.51.100.1",
            ["Records[1].Name"] = "www", ["Records[1].Type"] = "A", ["Records[1].Value"] = "999.1.1.1",
        }));
        Assert.AreEqual(0, await h.Query(db => db.DomainOperations.CountAsync()), "Con una fila mal no se pide nada.");
        var ok = await Body(await c.Submit(page, $"{page}?handler=Zone", new()
        {
            ["Records[0].Name"] = "@", ["Records[0].Type"] = "A", ["Records[0].Value"] = "203.0.113.10", ["Records[0].Ttl"] = "600",
            ["Records[1].Name"] = "www", ["Records[1].Type"] = "CNAME", ["Records[1].Value"] = "ejemplo.com", ["Records[1].Remove"] = "true",
            ["Records[2].Name"] = "@", ["Records[2].Type"] = "MX", ["Records[2].Value"] = "mail.proveedor.ar", ["Records[2].Priority"] = "10",
            ["Records[3].Name"] = "", ["Records[3].Type"] = "A", ["Records[3].Value"] = "",
        }));
        var pending = await c.Html(page);
        await h.RunJobs();
        var forwards = await Body(await c.Submit(page, $"{page}?handler=Forwards", new() { ["Forwards[0].Mailbox"] = "Info", ["Forwards[0].To"] = "ana@example.net" }));
        var privacy = await Body(await c.Submit($"/dominios/{id}", $"/dominios/{id}?handler=Privacy", new() { ["enabled"] = "true" }));
        await h.RunJobs();
        using var other = await c.Get($"/dominios/{foreign}/dns");

        StringAssert.Contains(domainPage, $"/dominios/{id}/dns");
        StringAssert.Contains(domainPage, "Protección de identidad");
        StringAssert.Contains(domainPage, "/dominios/nuevo#transferir", "El menú del portal lleva a transferir.");
        StringAssert.Contains(view, "198.51.100.1");
        StringAssert.Contains(bad, "Revisá las filas marcadas");
        StringAssert.Contains(bad, "999.1.1.1");
        StringAssert.Contains(ok, "Mandamos los registros al registrar");
        StringAssert.Contains(pending, "Hay un cambio de registros en camino");
        StringAssert.Contains(pending, "203.0.113.10");
        CollectionAssert.AreEqual(new[] { "@ A 203.0.113.10 600", "@ MX mail.proveedor.ar 3600" }, s.Fake.Domains["ejemplo.com"].Records.Select(r => $"{r.Name} {r.Type} {r.Value} {r.Ttl}").ToArray());
        StringAssert.Contains(forwards, "Mandamos los reenvíos al registrar");
        Assert.AreEqual("info ana@example.net", string.Join(";", s.Fake.Domains["ejemplo.com"].Forwards.Select(f => $"{f.Mailbox} {f.To}")));
        StringAssert.Contains(privacy, "Pedimos la protección de identidad");
        Assert.IsTrue(s.Fake.Domains["ejemplo.com"].Privacy);
        Assert.IsTrue((await h.Query(db => db.Domains.SingleAsync(d => d.Id == id))).IdProtection);
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
    }

    [TestMethod]
    public async Task Opciones_ConPrecio_ElPedidoLasOfrece_LaFichaLasContrata_YLaTablaDeDnsLasEspera()
    {
        await using var s = await Start();
        var h = s.H;
        await h.Use<TldPricing, bool>(async p =>
        {
            await p.SetOptionPrice(DomainOptions.Dns, Currency.Ars, 1590m, new DateOnly(2026, 1, 1));
            return true;
        });
        await h.PortalUser("titular@example.com", h.AccountA);
        await h.Staff("dominios@example.com", StaffPermissions.DomainsRead, StaffPermissions.DomainsManage);
        var id = await Active(s, h.AccountA, "ejemplo.com");
        using var c = h.NewClient();
        (await c.Login("titular@example.com")).Dispose();

        var order = await c.Html("/dominios/nuevo?nombre=otro.com");
        var locked = await c.Html($"/dominios/{id}/dns");
        var detail = await c.Html($"/dominios/{id}");
        using var contract = await c.Submit($"/dominios/{id}", $"/dominios/{id}?handler=Option", new() { ["option"] = "dns", ["enabled"] = "true" });
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Lines.Any(l => l.DomainId == id)));
        var waiting = await c.Html($"/dominios/{id}");

        StringAssert.Contains(order, "Gestión de DNS (");
        StringAssert.Contains(locked, "La gestión de DNS tiene un cargo de");
        StringAssert.Contains(detail, "Opciones del dominio");
        StringAssert.Contains(detail, "Contratar por");
        Assert.AreEqual($"/facturas/{invoice.Id}", Location(contract), "Contratar lleva a pagar la factura.");
        StringAssert.Contains(waiting, $"Se activa al pagar la factura {invoice.Id}");

        using var staff = await UiTests.StaffSession(h, "dominios@example.com");
        var prices = await Body(await staff.Submit("/admin/dominios/precios", "/admin/dominios/precios?handler=Option", new() { ["moneda"] = "ARS", ["option"] = "privacy", ["optionPrice"] = "2000", ["optionFrom"] = "2026-01-01" }));
        StringAssert.Contains(prices, "Opciones del dominio en ARS");
        Assert.AreEqual(2000m, (await h.Use<TldPricing, IReadOnlyDictionary<string, decimal>>(p => p.OptionPrices(Currency.Ars)))[DomainOptions.Privacy]);
        StringAssert.Contains(await staff.Html($"/admin/dominios/{id}"), "Se activa al pagar la factura");
    }

    [TestMethod]
    public async Task Admin_MenuClasico_DnsConPermisos_ConsultaDeDominios_YRegistradores()
    {
        await using var s = await Start();
        var h = s.H;
        await h.Staff("dominios@example.com", StaffPermissions.AccountsRead, StaffPermissions.DomainsRead, StaffPermissions.DomainsManage);
        await h.Staff("lector@example.com", StaffPermissions.DomainsRead);
        var id = await Active(s, h.AccountA, "ejemplo.com");
        s.Fake.Domains["ejemplo.com"].Records = [new("@", "A", "198.51.100.1")];
        var page = $"/admin/dominios/{id}/dns";
        var instance = s.Module.InstanceId.ToString(CultureInfo.InvariantCulture);
        using var c = await UiTests.StaffSession(h, "dominios@example.com");

        var menu = await c.Html("/admin/dominios");
        var detail = await c.Html($"/admin/dominios/{id}");
        var dns = await c.Html(page);
        var saved = await Body(await c.Submit(page, $"{page}?handler=Zone", new() { ["Records[0].Name"] = "www", ["Records[0].Type"] = "A", ["Records[0].Value"] = "203.0.113.20" }));
        await h.RunJobs();
        var lookup = await c.Html("/admin/dominios/consulta?q=Ejemplo.COM");
        var invalid = await c.Html("/admin/dominios/consulta?q=no+es+un+dominio");
        var registrars = await c.Html("/admin/registradores");
        var tested = await Body(await c.Submit("/admin/registradores", "/admin/registradores?handler=Test", new() { ["instance"] = instance }));

        using var reader = await UiTests.StaffSession(h, "lector@example.com");
        var readerMenu = await reader.Html("/admin/dominios");
        var readOnly = await reader.Html(page);
        using var blocked = await reader.Submit(page, $"{page}?handler=Zone", new() { ["Records[0].Name"] = "@", ["Records[0].Type"] = "A", ["Records[0].Value"] = "203.0.113.30" });
        using var noTest = await reader.Submit("/admin/registradores", "/admin/registradores?handler=Test", new() { ["instance"] = instance });

        foreach (var item in new[] { "Clientes", "Ver clientes", "Utilidades", "Consulta de dominios", "Configuración", "Dominios y precios por TLD", "Registradores" })
        {
            StringAssert.Contains(menu, item);
        }

        Assert.IsFalse(readerMenu.Contains("Ver clientes", StringComparison.Ordinal), "Sin accounts.read no aparece lo que no puede abrir.");
        StringAssert.Contains(detail, page);
        StringAssert.Contains(dns, "198.51.100.1");
        StringAssert.Contains(saved, "Mandamos los registros al registrar");
        Assert.AreEqual("www A 203.0.113.20", string.Join(";", s.Fake.Domains["ejemplo.com"].Records.Select(r => $"{r.Name} {r.Type} {r.Value}")));
        StringAssert.Contains(lookup, "ejemplo.com");
        StringAssert.Contains(lookup, $"/admin/dominios/{id}");
        StringAssert.Contains(lookup, "el sistema");
        StringAssert.Contains(invalid, "Eso no es un nombre de dominio");
        StringAssert.Contains(registrars, "dns, email_forwarding");
        StringAssert.Contains(tested, "La configuración anda");
        StringAssert.Contains(readOnly, "disabled");
        Assert.IsFalse(readOnly.Contains("Guardar los registros", StringComparison.Ordinal));
        StringAssert.Contains(Location(blocked), "/estado/403");
        StringAssert.Contains(Location(noTest), "/estado/403");
        Assert.AreEqual(1, await h.Query(db => db.DomainOperations.CountAsync(o => o.Operation == "set_dns")), "El lector no cambia la zona.");
    }

    [TestMethod]
    public async Task Admin_ListaDeDominios_AccionMasivaConConfirmacion_YExportacionCsv()
    {
        await using var s = await Start();
        var h = s.H;
        await h.Staff("dominios@example.com", StaffPermissions.DomainsRead, StaffPermissions.DomainsManage);
        var one = (await Active(s, h.AccountA, "uno.com")).ToString(CultureInfo.InvariantCulture);
        var two = (await Active(s, h.AccountA, "dos.com")).ToString(CultureInfo.InvariantCulture);
        using var c = await UiTests.StaffSession(h, "dominios@example.com");
        const string page = "/admin/dominios?q=.com";

        var unconfirmed = await Body(await c.Submit(page, "/admin/dominios?handler=Bulk", new() { ["ids[0]"] = one, ["action"] = "renew-off", ["q"] = ".com" }));
        Assert.IsTrue(await h.Query(db => db.Domains.AllAsync(d => d.AutoRenew)), "Sin confirmar no se toca nada.");
        var done = await Body(await c.Submit(page, "/admin/dominios?handler=Bulk", new() { ["ids[0]"] = one, ["ids[1]"] = two, ["action"] = "renew-off", ["confirm"] = "true", ["q"] = ".com" }));
        using var csv = await c.Get("/admin/dominios?handler=Csv&q=.com");
        var text = await csv.Content.ReadAsStringAsync();
        (await c.Get("/idioma/en?volver=/admin/dominios")).Dispose();
        var english = await (await c.Get("/admin/dominios?handler=Csv&q=.com")).Content.ReadAsStringAsync();

        StringAssert.Contains(unconfirmed, "confirmá con la casilla");
        StringAssert.Contains(done, "se aplicó a 2 dominios y 0");
        Assert.IsFalse(await h.Query(db => db.Domains.AnyAsync(d => d.AutoRenew)));
        Assert.AreEqual("text/csv", csv.Content.Headers.ContentType!.MediaType);
        StringAssert.StartsWith(text.TrimStart('﻿'), "id;dominio;cuenta");
        StringAssert.Contains(text, ";uno.com;");
        StringAssert.Contains(text, ";dos.com;");
        StringAssert.EndsWith(text.TrimEnd('\r', '\n').Split('\n')[^1].TrimEnd('\r'), ";no");
        StringAssert.StartsWith(english.TrimStart('﻿'), "id;domain;account;account_id;status;registry_status;expires_on;paid_through;registrar;auto_renew");
        StringAssert.EndsWith(english.TrimEnd('\r', '\n').Split('\n')[^1].TrimEnd('\r'), ";no");
    }

    [TestMethod]
    public async Task Admin_Fallidas_LaRenovacionPagaSinRenovarQuedaPasadoElMes_YLoDemasSaleALos30Dias()
    {
        await using var s = await Start();
        var h = s.H;
        await h.Staff("dominios@example.com", StaffPermissions.AccountsRead, StaffPermissions.DomainsRead, StaffPermissions.DomainsManage);
        var paid = await Active(s, h.AccountA, "pagado.com");
        var other = await Active(s, h.AccountA, "otro.com");
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await h.Db(async db =>
        {
            // Pagó la renovación hasta 2028 y el registro la rechazó hace 40 días: sigue venciendo en 2027.
            var domain = await db.Domains.SingleAsync(d => d.Id == paid);
            domain.TakeNextPeriod();
            domain.RenewalPaid(DateOnly.FromDateTime(DateTime.UtcNow));
            var renew = new DomainOperation(paid, "renew", """{"years":1,"current_expiry":"2027-06-01"}""", "renew:2027-06-01", old);
            renew.Fail("rejected: Sin saldo en la cuenta del revendedor.", old);
            var nameservers = new DomainOperation(other, "set_nameservers", """{"nameservers":["ns1.otro.net","ns2.otro.net"]}""", "set_nameservers:viejo", old);
            nameservers.Fail("rejected: El nameserver no existe.", old);
            db.DomainOperations.AddRange(renew, nameservers);
            await db.SaveChangesAsync();
        });
        using var c = await UiTests.StaffSession(h, "dominios@example.com");

        var failed = await c.Html("/admin/dominios?vista=fallidas");
        await h.Db(async db =>
        {
            (await db.Domains.SingleAsync(d => d.Id == paid)).ExpiresOn = new DateOnly(2028, 6, 1);
            await db.SaveChangesAsync();
        });

        StringAssert.Contains(failed, "pagado.com");
        Assert.IsFalse(failed.Contains("otro.com", StringComparison.Ordinal), "Pasados 30 días, una falla que no es de una renovación paga sale de la cola.");
        StringAssert.Contains(await c.Html($"/admin/dominios/{other}"), "Operación fallida", "En la ficha se puede reintentar igual.");
        Assert.IsFalse((await c.Html("/admin/dominios?vista=fallidas")).Contains("pagado.com", StringComparison.Ordinal), "La sincronización trajo el vencimiento pago: sale.");
    }

    [TestMethod]
    public async Task Admin_ColaPorTrabajoPendiente_ReintentaLaTransferencia_EImportaPrecios()
    {
        await using var s = await Start();
        var h = s.H;
        s.Fake.TransfersPending = false;
        await h.Staff("dominios@example.com", StaffPermissions.AccountsRead, StaffPermissions.DomainsRead, StaffPermissions.DomainsManage);
        await h.Staff("lector@example.com", StaffPermissions.DomainsRead);
        await h.Staff("cuentas@example.com", StaffPermissions.AccountsRead);
        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountA, new DomainOrder("traer.com", AuthCode: "EPP-MALO")));
        await h.Use<PaymentService, long>(p => p.Record(new PaymentInput(h.AccountA, "manual:transferencia", "T-1", Currency.Ars, 14000m, PaymentStatus.Approved, [placed.InvoiceId!.Value])));
        await h.RunJobs();
        var page = $"/admin/dominios/{placed.DomainId}";
        using var c = await UiTests.StaffSession(h, "dominios@example.com");

        var queue = await c.Html("/admin/dominios");
        var failed = await c.Html("/admin/dominios?vista=fallidas");
        var detail = await c.Html(page);
        var retried = await Body(await c.Submit(page, $"{page}?handler=Retry", new() { ["authCode"] = "EPP-CORRECTO" }));
        await h.RunJobs();

        StringAssert.Contains(queue, "Con operaciones fallidas");
        StringAssert.Contains(failed, "traer.com");
        StringAssert.Contains(detail, "rejected");
        StringAssert.Contains(detail, "Operación fallida");
        StringAssert.Contains(detail, "Cancelar el pedido");
        StringAssert.Contains(retried, "Volvimos a pedirlo");
        Assert.AreEqual(DomainStatus.Active, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId))).Status);
        Assert.IsFalse((await c.Html("/admin/dominios?vista=fallidas")).Contains("traer.com", StringComparison.Ordinal), "Reintentada y terminada, sale de las fallidas.");
        Assert.IsFalse((await c.Html(page)).Contains("Operación fallida", StringComparison.Ordinal));
        StringAssert.Contains(await c.Html($"/admin/cuentas/{h.AccountA}"), "traer.com");

        var prices = await c.Html("/admin/dominios/precios");
        var instance = s.Module.InstanceId.ToString(CultureInfo.InvariantCulture);
        var import = new Dictionary<string, string> { ["moneda"] = "ARS", ["import.InstanceId"] = instance, ["import.ExchangeRate"] = "1000", ["import.MarginPercent"] = "30", ["import.TaxPercent"] = "21", ["import.RoundTo"] = "100" };
        var preview = await Body(await c.Submit("/admin/dominios/precios", "/admin/dominios/precios?handler=Preview", new(import)));
        Assert.AreEqual(2, await h.Query(db => db.TldPrices.CountAsync()), "La propuesta no guarda nada.");
        var applied = await Body(await c.Submit("/admin/dominios/precios", "/admin/dominios/precios?handler=Apply", new(import)));

        StringAssert.Contains(prices, "15.000,00");
        StringAssert.Contains(preview, "Propuesta de precios: 2");
        StringAssert.Contains(preview, "17.400,00");
        StringAssert.Contains(applied, "se guardaron los precios importados");
        Assert.AreEqual(17400m, await h.Query(db => db.TldPrices.Where(p => p.Tld == ".net").Select(p => p.Register).SingleAsync()));
        var csv = await Body(await c.Submit("/admin/dominios/precios", "/admin/dominios/precios?handler=CsvPreview", new()
        {
            ["moneda"] = "ARS", ["csvInstance"] = instance, ["csv"] = "tld;years;register;renew;transfer\n.com.ar;1;9.000,50;9000;",
        }));
        StringAssert.Contains(csv, "Propuesta de precios: 1");
        StringAssert.Contains(csv, "9.000,50");
        StringAssert.Contains(csv, "handler=CsvApply");
        var byTld = await Body(await c.Submit("/admin/dominios/precios", "/admin/dominios/precios?handler=TldOptions", new() { ["moneda"] = "ARS", ["optionsTld"] = ".NET", ["offered[0]"] = "dns" }));
        var net = await h.Query(db => db.TldOptions.SingleAsync(t => t.Tld == ".net"));
        StringAssert.Contains(byTld, "Opciones por extensión");
        Assert.AreEqual((true, false, false), (net.Dns, net.EmailForwarding, net.Privacy));

        using var reader = await UiTests.StaffSession(h, "lector@example.com");
        StringAssert.Contains(await reader.Html(page), "traer.com");
        using var sync = await reader.Submit(page, $"{page}?handler=Sync", []);
        StringAssert.Contains(Location(sync), "/estado/403");
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.DomainSync)));
        using var accounts = await UiTests.StaffSession(h, "cuentas@example.com");
        using var blocked = await accounts.Get("/admin/dominios");
        StringAssert.Contains(Location(blocked), "/estado/403");
    }
}
