using System.Text;
using System.Text.Json.Nodes;
using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>
/// Dominios con un registrar con API, de punta a punta contra un registrar falso que habla el protocolo: pedido y pago,
/// registro, transferencia con código, operaciones, precios por TLD, búsqueda y sincronización diaria.
/// </summary>
[TestClass]
public sealed class DomainRegistrarTests
{
    private sealed record Setup(CoreHarness H, TestModule Module, FakeRegistrarModule Fake, SecretBox Box) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Module.DisposeAsync();
            await H.DisposeAsync();
        }
    }

    /// <summary>El núcleo con el registrar falso vendiendo .com en pesos y una cuenta con el titular completo.</summary>
    private static async Task<Setup> Start(Action<BillingOptions>? configure = null, FakeRdap? rdap = null, params string[] capabilities)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, o =>
        {
            o.DomainNameservers = ["ns1.proveedor.ar", "ns2.proveedor.ar"];
            configure?.Invoke(o);
        }, rdap: rdap);
        var fake = new FakeRegistrarModule();
        var module = await TestModule.Start(fake.Definition(capabilities), h, box);
        await h.Db(db => FakeRegistrarModule.Seed(db, module.InstanceId, h.AccountId));
        return new Setup(h, module, fake, box);
    }

    /// <summary>Un dominio ya activo en la instancia del registrar falso, que también lo tiene en su cuenta.</summary>
    private static async Task<long> Active(Setup s, string name = "ejemplo.com", DateOnly? expires = null)
    {
        var until = expires ?? new DateOnly(2027, 6, 1);
        s.Fake.Domains[name] = new FakeRegistrarModule.FakeEntry { Expires = until, Nameservers = ["ns1.viejo.net", "ns2.viejo.net"] };
        long id = 0;
        await s.H.Db(async db =>
        {
            var domain = new RegisteredDomain(s.H.AccountId, name, s.Module.InstanceId, Currency.Ars, 16000m, s.H.Clock.Now);
            domain.Start(until);
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            id = domain.Id;
        });
        return id;
    }

    private static string[] Hosts(string json) => [.. JsonNode.Parse(json)!.AsArray().Select(n => n!.ToString())];

    private static Task Pay(CoreHarness h, decimal amount, string reference, long? invoice = null) =>
        h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", reference, Currency.Ars, amount, PaymentStatus.Approved, invoice is { } id ? [id] : null)));

    private static Task Run<T>(CoreHarness h, Func<T, Task> work)
        where T : notnull => h.Use<T, bool>(async s =>
        {
            await work(s);
            return true;
        });

    [TestMethod]
    public async Task Registro_SeFacturaYRecienAlPagarseRegistraConElTitularDeLaCuenta()
    {
        await using var s = await Start();
        var h = s.H;

        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("Nuevo.COM")));
        await h.RunJobs();

        Assert.IsFalse(s.Fake.Calls.Any(c => c.Operation == "register"), "Sin pagar no se registra.");
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(15000m, invoice.Total);
        Assert.AreEqual(placed.DomainId, invoice.Lines.Single().DomainId);
        Assert.AreEqual("Registro del dominio nuevo.com, 1 año", invoice.Lines.Single().Description);
        var pending = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.AreEqual((DomainStatus.Pending, 16000m), (pending.Status, pending.RecurringAmount));

        await Pay(h, 15000m, "T-1");
        await h.RunJobs();

        var register = s.Fake.Calls.Single(c => c.Operation == "register");
        Assert.AreEqual("nuevo.com", register.Input["name"]!.ToString());
        CollectionAssert.AreEqual(new[] { "ns1.proveedor.ar", "ns2.proveedor.ar" }, register.Input["nameservers"]!.AsArray().Select(n => n!.ToString()).ToArray());
        var registrant = register.Context["contacts"]!["registrant"]!;
        Assert.AreEqual(("Cliente", "de prueba", "+54.1155551234", "Av. Siempre Viva 742", "AR"), (registrant["first_name"]!.ToString(), registrant["last_name"]!.ToString(), registrant["phone"]!.ToString(), registrant["address1"]!.ToString(), registrant["country"]!.ToString()));
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.AreEqual(DomainStatus.Active, domain.Status);
        Assert.AreEqual(new DateOnly(2027, 11, 2), domain.ExpiresOn);
        Assert.AreEqual((new DateOnly(2027, 11, 2), new DateOnly(2027, 11, 2)), (domain.NextDueDate, domain.PaidThrough));
        StringAssert.Contains(domain.ExternalIds, "R-nuevo.com");
        Assert.AreEqual(DomainOperationStatus.Completed, (await h.Query(db => db.DomainOperations.SingleAsync())).Status);

        var again = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com"))));
        StringAssert.Contains(again.Message, "ya está en el sistema");
    }

    [TestMethod]
    public async Task Registro_PorDosAños_UsaElPrecioDeDosAños_YRenuevaPorDos()
    {
        await using var s = await Start();
        var h = s.H;

        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("dos.com", Years: 2)));

        Assert.AreEqual(29000m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId))).Total);
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.AreEqual((2, 31000m), (domain.Years, domain.RecurringAmount));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("tres.com", Years: 3))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("otro.org"))));
    }

    [TestMethod]
    public async Task Pedido_ConsultaElNombreAntesDeFacturar_YSiNadieLoSabeSigue()
    {
        await using var s = await Start();
        var h = s.H;
        s.Fake.Premium.Add("caro.com");

        var taken = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("tomado.com"))));
        var premium = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("caro.com"))));
        var free = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("libre.com", AuthCode: "EPP-CORRECTO"))));

        StringAssert.Contains(taken.Message, "no está disponible");
        StringAssert.Contains(premium.Message, "premium");
        StringAssert.Contains(free.Message, "no está registrado");
        Assert.AreEqual((0, 0), (await h.Query(db => db.Documents.CountAsync()), await h.Query(db => db.Domains.CountAsync())));

        // El registrar no contesta y el RDAP está apagado: nadie sabe, el pedido sigue y lo verifica el registrar.
        s.Fake.ChecksFail = true;
        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com")));
        Assert.IsNotNull(placed.InvoiceId);
    }

    [TestMethod]
    public async Task Registro_SinDomicilioDelTitular_SeAvisaAntesDeFacturar()
    {
        await using var s = await Start();
        var h = s.H;
        await h.Db(async db =>
        {
            var tax = await db.TaxIdentities.SingleAsync(t => t.AccountId == h.AccountId);
            tax.Street = null;
            await db.SaveChangesAsync();
        });

        var error = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com"))));
        var withOwn = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com", Registrant: new JsonObject
        {
            ["first_name"] = "Ana", ["last_name"] = "Gómez", ["email"] = "ana@example.com", ["phone"] = "1155551234",
            ["address1"] = "Calle 1", ["city"] = "La Plata", ["postal_code"] = "B1900", ["country"] = "ar",
        })));

        StringAssert.Contains(error.Message, "address1");
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Id != withOwn.InvoiceId)));
        var stored = JsonNode.Parse((await h.Query(db => db.Domains.SingleAsync())).Registrant!)!;
        Assert.AreEqual(("+54.1155551234", "AR"), (stored["phone"]!.ToString(), stored["country"]!.ToString()));
    }

    [TestMethod]
    public async Task Transferencia_ElCodigoQuedaCifrado_ViajaSoloEnLaLlamada_YTerminaAlSoltarseEnElOrigen()
    {
        await using var s = await Start();
        var h = s.H;

        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: " EPP-CORRECTO ")));

        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.IsFalse(Encoding.UTF8.GetString(domain.AuthCode!).Contains("EPP-CORRECTO", StringComparison.Ordinal));
        Assert.AreEqual("EPP-CORRECTO", s.Box.Decrypt(domain.AuthCode!, "domains.domains", "auth_code", domain.Id));
        Assert.IsFalse((await h.Query(db => db.DomainOperations.SingleAsync())).Input.Contains("EPP", StringComparison.Ordinal));
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual((14000m, "Transferencia del dominio traer.com, con 1 año de renovación"), (invoice.Total, invoice.Lines.Single().Description));

        await Pay(h, 14000m, "T-2");
        await h.RunJobs();

        Assert.AreEqual("EPP-CORRECTO", s.Fake.Calls.Single(c => c.Operation == "transfer").Input["auth_code"]!.ToString());
        Assert.AreEqual(DomainStatus.Pending, (await h.Query(db => db.Domains.SingleAsync())).Status);
        Assert.IsNotNull((await h.Query(db => db.DomainOperations.SingleAsync())).PendingOperation);

        s.Fake.Release("traer.com");
        h.Clock.Now = h.Clock.Now.AddHours(2);
        await h.RunJobs();

        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual(DomainStatus.Active, domain.Status);
        Assert.AreEqual(s.Fake.Domains["traer.com"].Expires, domain.ExpiresOn);
        Assert.AreEqual(domain.ExpiresOn, domain.NextDueDate);
        Assert.IsNull(domain.AuthCode, "El código con que se trajo ya se usó y no queda para mostrar.");
        Assert.AreEqual(DomainOperationStatus.Completed, (await h.Query(db => db.DomainOperations.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Transferencia_CodigoInvalido_FallaLaOperacion_YElDominioQuedaPendienteParaElStaff()
    {
        await using var s = await Start();
        var h = s.H;
        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-MALO")));

        await Pay(h, 14000m, "T-3");
        await h.RunJobs();

        var op = await h.Query(db => db.DomainOperations.SingleAsync());
        Assert.AreEqual(DomainOperationStatus.Failed, op.Status);
        StringAssert.Contains(op.LastError, "rejected");
        Assert.AreEqual(DomainStatus.Pending, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId))).Status);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("otro.com"))));
    }

    [TestMethod]
    public async Task Operaciones_BloqueoCodigoEppTitularYNameservers_PasanPorLaColaYQuedanEnElDominio()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s);

        await Run<DomainService>(h, d => d.SetLock(id, true));
        await Run<DomainService>(h, d => d.RequestAuthCode(id));
        await Run<DomainService>(h, d => d.SetContacts(id, new JsonObject
        {
            ["first_name"] = "Ana", ["last_name"] = "Gómez", ["email"] = "nuevo@example.com", ["phone"] = "+54.1144443333",
            ["address1"] = "Calle 1", ["city"] = "La Plata", ["postal_code"] = "B1900", ["country"] = "AR",
        }));
        await Run<DomainService>(h, d => d.SetNameservers(id, ["NS1.Otro.net", "ns2.otro.net", "ns2.otro.net"]));
        await h.RunJobs();

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.IsTrue(domain.Locked);
        Assert.IsTrue(s.Fake.Domains["ejemplo.com"].Locked);
        Assert.AreEqual("EPP-DEL-REGISTRAR", await h.Use<DomainService, string?>(d => Task.FromResult(d.RevealAuthCode(domain))));
        Assert.IsFalse((await h.Query(db => db.DomainOperations.Where(o => o.Operation == "get_auth_code").Select(o => o.Result).SingleAsync()))!.Contains("EPP", StringComparison.Ordinal));
        Assert.AreEqual("nuevo@example.com", s.Fake.Domains["ejemplo.com"].RegistrantEmail);
        StringAssert.Contains(domain.Registrant, "nuevo@example.com");
        CollectionAssert.AreEqual(new[] { "ns1.otro.net", "ns2.otro.net" }, Hosts(domain.Nameservers));
        Assert.IsTrue(await h.Query(db => db.DomainOperations.AllAsync(o => o.Status == DomainOperationStatus.Completed)));

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetNameservers(id, ["solo.uno.net"])));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetContacts(id, new JsonObject { ["first_name"] = "Sin datos" })));
    }

    [TestMethod]
    public async Task Operaciones_LoQueElRegistrarNoDeclaraNoSePide()
    {
        await using var s = await Start(capabilities: ["registrar.pricing"]);
        var h = s.H;
        var id = await Active(s);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetLock(id, true)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RequestAuthCode(id)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetContacts(id, new JsonObject
        {
            ["first_name"] = "Ana", ["last_name"] = "Gómez", ["email"] = "a@example.com", ["phone"] = "1155551234",
            ["address1"] = "Calle 1", ["city"] = "La Plata", ["postal_code"] = "B1900", ["country"] = "AR",
        })));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetDns(id, [new DnsRecord("@", "A", "203.0.113.10")])));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Dns(id)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetForwards(id, [new EmailForward("info", "ana@example.net")])));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetPrivacy(id, true)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com", Privacy: true))));
        Assert.AreEqual(0, await h.Query(db => db.DomainOperations.CountAsync()));
    }

    [TestMethod]
    public async Task Zona_SeLeeDelRegistrar_YSeCambiaEnteraPorLaCola()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s);
        s.Fake.Domains["ejemplo.com"].Records = [new DnsRecord("@", "A", "198.51.100.1")];

        var before = await h.Use<DomainService, IReadOnlyList<DnsRecord>>(d => d.Dns(id));
        await Run<DomainService>(h, d => d.SetDns(id, [new DnsRecord("WWW.ejemplo.com", "a", "203.0.113.10"), new DnsRecord("@", "MX", "Mail.Proveedor.ar.", Priority: 5), new DnsRecord("@", "TXT", "\"v=spf1 mx -all\"")]));
        var queued = await h.Query(db => db.DomainOperations.SingleAsync());
        var invalid = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetDns(id, [new DnsRecord("@", "CNAME", "otro.example.net")])));
        await h.RunJobs();

        Assert.AreEqual("@ A 198.51.100.1", string.Join(";", before.Select(r => $"{r.Name} {r.Type} {r.Value}")));
        Assert.AreEqual(("set_dns", DomainOperationStatus.Pending), (queued.Operation, queued.Status));
        StringAssert.Contains(invalid.Message, "CNAME");
        CollectionAssert.AreEqual(
            new[] { "@ MX mail.proveedor.ar 5", "@ TXT v=spf1 mx -all ", "www A 203.0.113.10 " },
            s.Fake.Domains["ejemplo.com"].Records.Select(r => $"{r.Name} {r.Type} {r.Value} {r.Priority}").ToArray(),
            "La zona va entera, normalizada y en orden.");
        Assert.AreEqual(DomainOperationStatus.Completed, (await h.Query(db => db.DomainOperations.SingleAsync())).Status);
        Assert.AreEqual(3, (await h.Use<DomainService, IReadOnlyList<DnsRecord>>(d => d.Dns(id))).Count);

        s.Fake.ReadsFail = true;
        var down = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Dns(id)));
        StringAssert.Contains(down.Message, "upstream_unavailable");
    }

    [TestMethod]
    public async Task ReenviosYProteccion_PasanPorLaCola_YLaProteccionQuedaEnElDominioYLaTraeLaSincronizacion()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s);

        await Run<DomainService>(h, d => d.SetForwards(id, [new EmailForward("Info", "ana@example.net"), new EmailForward("ventas", "ventas@example.org")]));
        await Run<DomainService>(h, d => d.SetPrivacy(id, true));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.SetForwards(id, [new EmailForward("info", "no es un correo")])));
        await h.RunJobs();

        CollectionAssert.AreEqual(new[] { "info ana@example.net", "ventas ventas@example.org" }, s.Fake.Domains["ejemplo.com"].Forwards.Select(f => $"{f.Mailbox} {f.To}").ToArray());
        Assert.AreEqual(2, (await h.Use<DomainService, IReadOnlyList<EmailForward>>(d => d.Forwards(id))).Count);
        Assert.IsTrue((await h.Query(db => db.Domains.SingleAsync())).IdProtection);

        // Alguien la apagó en el panel del registrar: la sincronización lo trae.
        s.Fake.Domains["ejemplo.com"].Privacy = false;
        await Run<DomainService>(h, d => d.SyncNow(id));
        await h.RunJobs();
        Assert.IsFalse((await h.Query(db => db.Domains.SingleAsync())).IdProtection);
    }

    [TestMethod]
    public async Task Registro_ConProteccion_LaPideAlRegistrar_YQuedaEnElDominio()
    {
        await using var s = await Start();
        var h = s.H;

        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("privado.com", Privacy: true)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-CORRECTO", Privacy: true))));
        await Pay(h, 15000m, "T-11", placed.InvoiceId);
        await h.RunJobs();

        Assert.IsTrue(s.Fake.Calls.Single(c => c.Operation == "register").Input["privacy"]!.GetValue<bool>());
        Assert.IsTrue(s.Fake.Domains["privado.com"].Privacy);
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.AreEqual((DomainStatus.Active, true), (domain.Status, domain.IdProtection));
    }

    /// <summary>Precios anuales de las opciones: la gestión de DNS y la protección con cargo, el reenvío gratis.</summary>
    private static async Task OptionPrices(CoreHarness h)
    {
        await Run<TldPricing>(h, p => p.SetOptionPrice(DomainOptions.Dns, Currency.Ars, 1590m, new DateOnly(2026, 1, 1)));
        await Run<TldPricing>(h, p => p.SetOptionPrice(DomainOptions.Privacy, Currency.Ars, 2000m, new DateOnly(2026, 1, 1)));
        await Run<TldPricing>(h, p => p.SetOptionPrice(DomainOptions.Privacy, Currency.Ars, 9999m, new DateOnly(2027, 1, 1)));
    }

    [TestMethod]
    public async Task Opciones_ConPrecio_SeContratanAlPedir_SumanAlRegistroYALaRenovacion()
    {
        await using var s = await Start();
        var h = s.H;
        await OptionPrices(h);
        await Run<TldPricing>(h, p => p.SetOptionPrice(DomainOptions.Dns, Currency.Ars, 1600m, new DateOnly(2026, 1, 1)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<TldPricing>(h, p => p.SetOptionPrice("hosting", Currency.Ars, 1m, new DateOnly(2026, 1, 1))));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<TldPricing>(h, p => p.SetOptionPrice(DomainOptions.Dns, Currency.Ars, -1m, new DateOnly(2026, 1, 1))));
        var prices = await h.Use<TldPricing, IReadOnlyDictionary<string, decimal>>(p => p.OptionPrices(Currency.Ars));
        Assert.AreEqual((1600m, 0m, 2000m), (prices[DomainOptions.Dns], prices[DomainOptions.EmailForwarding], prices[DomainOptions.Privacy]), "La misma fecha corrige y la futura todavía no rige.");

        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("opciones.com", Years: 2, Privacy: true, Dns: true, EmailForwarding: true)));
        var line = (await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == placed.InvoiceId))).Lines.Single();
        Assert.AreEqual(29000m + ((1600m + 2000m) * 2), line.Amount, "Cada opción con precio suma su precio anual por los años del pedido.");
        Assert.AreEqual("Registro del dominio opciones.com, 2 años + gestión de DNS + reenvío de correo + protección de identidad", line.Description);

        await Pay(h, line.Amount, "T-30", placed.InvoiceId);
        await h.RunJobs();
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.AreEqual((true, true, true), (domain.DnsManagement, domain.EmailForwarding, domain.IdProtection));
        Assert.AreEqual(31000m + ((1600m + 2000m) * 2), domain.RecurringAmount, "La renovación lleva las opciones con su precio congelado.");
        Assert.IsTrue(domain.HasPaidOption(DomainOptions.Privacy) && !domain.HasPaidOption(DomainOptions.EmailForwarding));

        var renewal = await h.Use<DomainService, long?>(d => d.RenewNow(placed.DomainId));
        var renewalLine = (await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == renewal))).Lines.Single();
        Assert.AreEqual(domain.RecurringAmount, renewalLine.Amount);
        StringAssert.EndsWith(renewalLine.Description, "+ gestión de DNS + reenvío de correo + protección de identidad");

        // Lo que el registrar no ofrece no se vende, tampoco gratis.
        await using var bare = await Start(capabilities: ["registrar.transfer"]);
        var missing = await Assert.ThrowsExactlyAsync<ValidationException>(() => bare.H.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(bare.H.AccountId, new DomainOrder("sindns.com", EmailForwarding: true))));
        StringAssert.Contains(missing.Message, "reenvío de correo");
    }

    [TestMethod]
    public async Task Opciones_ConPrecio_DesdeElDominio_SeFacturaLoQueQueda_SeAplicaAlPagarse_YLaBajaLaSacaDeLaRenovacion()
    {
        await using var s = await Start();
        var h = s.H;
        await OptionPrices(h);
        var id = await Active(s, expires: new DateOnly(2027, 6, 1));
        var domain = await h.Query(db => db.Domains.SingleAsync());

        // Sin contratar, la zona no se lee ni se cambia. El reenvío es gratis y está siempre.
        var locked = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Dns(id)));
        StringAssert.Contains(locked.Message, "no tiene contratada");
        await Run<DomainService>(h, d => d.Forwards(id));
        var before = await h.Use<DomainService, IReadOnlyList<DomainOptionState>>(d => d.Options(domain));
        Assert.AreEqual("dns:1590:False, email_forwarding:0:True, privacy:2000:False", string.Join(", ", before.Select(o => $"{o.Option}:{o.Price:0}:{o.Usable}")));

        var invoiceId = await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true));
        var again = await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true));
        var line = (await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == invoiceId))).Lines.Single();
        var days = new DateOnly(2027, 6, 1).DayNumber - new DateOnly(2026, 11, 2).DayNumber;
        Assert.AreEqual(invoiceId, again, "Pedirla de nuevo con la factura sin pagar devuelve la misma factura.");
        Assert.AreEqual(Math.Round(1590m * days / 365m, 2, MidpointRounding.AwayFromZero), line.Amount, "Se cobra por día lo que queda hasta el vencimiento.");
        Assert.AreEqual("Gestión de DNS del dominio ejemplo.com, hasta el 01/06/2027", line.Description);
        Assert.AreEqual(invoiceId, (await h.Use<DomainService, IReadOnlyList<DomainOptionState>>(d => d.Options(domain))).Single(o => o.Option == DomainOptions.Dns).WaitingInvoice);
        await h.RunJobs();
        Assert.IsFalse((await h.Query(db => db.Domains.SingleAsync())).DnsManagement, "Se aplica recién con la factura paga.");

        await Pay(h, line.Amount, "T-31", invoiceId);
        await h.RunJobs();
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((true, 17590m), (domain.DnsManagement, domain.RecurringAmount));
        Assert.IsEmpty(await h.Use<DomainService, IReadOnlyList<DnsRecord>>(d => d.Dns(id)), "Contratada, la zona se lee.");

        // La protección con precio: se factura, al pagarse queda contratada y se le pide al registrar.
        var privacyInvoice = await h.Use<DomainService, long?>(d => d.SetPrivacy(id, true));
        Assert.IsFalse(s.Fake.Calls.Any(c => c.Operation == "set_privacy"));
        await Pay(h, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == privacyInvoice))).Total, "T-32", privacyInvoice);
        await h.RunJobs();
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((true, 19590m), (domain.IdProtection, domain.RecurringAmount));
        Assert.IsTrue(s.Fake.Domains["ejemplo.com"].Privacy);

        // Darlas de baja: dejan la renovación como estaba y la protección se apaga en el registrar, sin factura.
        Assert.IsNull(await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, false)));
        Assert.IsNull(await h.Use<DomainService, long?>(d => d.SetPrivacy(id, false)));
        await h.RunJobs();
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((false, false, 16000m), (domain.DnsManagement, domain.IdProtection, domain.RecurringAmount));
        Assert.AreEqual(2, await h.Query(db => db.Documents.CountAsync()), "Una factura por cada contratación y ninguna por las bajas.");
    }

    [TestMethod]
    public async Task Opciones_PorExtension_LaQueNoSeOfreceNoSePide_YElDominioQueLaTieneLaConserva()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2027, 6, 1));
        await h.Db(async db =>
        {
            (await db.Domains.SingleAsync()).ContractOption(DomainOptions.Dns, 0m);
            await db.SaveChangesAsync();
        });
        await Run<TldPricing>(h, p => p.SetTldOptions("COM", [DomainOptions.EmailForwarding]));
        var domain = await h.Query(db => db.Domains.SingleAsync());

        var order = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com", Privacy: true))));
        var privacy = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainService, long?>(d => d.SetPrivacy(id, true)));
        var options = await h.Use<DomainService, IReadOnlyList<DomainOptionState>>(d => d.Options(domain));
        await Run<DomainService>(h, d => d.Dns(id));

        StringAssert.Contains(order.Message, "No ofrecemos esta opción en .com: protección de identidad");
        StringAssert.Contains(privacy.Message, "No ofrecemos esta opción en .com");
        Assert.AreEqual("dns, email_forwarding", string.Join(", ", options.Select(o => o.Option)), "La gestión de DNS sigue en el dominio que la tiene, para usarla o darla de baja.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Privacy, true)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<TldPricing>(h, p => p.SetTldOptions(".com", ["hosting"])));

        Assert.IsNull(await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, false)));
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual("email_forwarding", string.Join(", ", (await h.Use<DomainService, IReadOnlyList<DomainOptionState>>(d => d.Options(domain))).Select(o => o.Option)), "Dada de baja, ya no se ofrece.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Dns(id)));
    }

    [TestMethod]
    public async Task Opciones_LaFacturaAnulada_NoTrabaLaOpcion_YSeVuelveAContratar()
    {
        await using var s = await Start();
        var h = s.H;
        await OptionPrices(h);
        var id = await Active(s, expires: new DateOnly(2027, 6, 1));
        var first = await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true));
        var lines = (await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == first))).Lines
            .Select(l => new CreditLine(l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, l.ServiceId)).ToList();
        await Run<CreditService>(h, c => c.IssueCreditNote(first!.Value, lines, "Pedida por error"));

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.IsNull((await h.Use<DomainService, IReadOnlyList<DomainOptionState>>(d => d.Options(domain))).Single(o => o.Option == DomainOptions.Dns).WaitingInvoice);
        var second = await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true));

        Assert.IsNotNull(second);
        Assert.AreNotEqual(first, second, "La factura anulada no se devuelve: se hace otra.");
        Assert.AreEqual(1, await h.Query(db => db.DomainOperations.CountAsync(o => o.Operation == DomainJobs.AddOption && o.Status == DomainOperationStatus.Failed)));
    }

    [TestMethod]
    public async Task Opciones_EnGracia_NoQuedaNadaPorCobrar_SeAplicaEnseguida_YVaEnLaRenovacion()
    {
        await using var s = await Start();
        var h = s.H;
        await OptionPrices(h);
        var id = await Active(s, expires: new DateOnly(2026, 10, 25));

        Assert.IsNull(await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true)));
        Assert.IsNull(await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true)), "La que ya está en la cola no se pide otra vez.");
        await h.RunJobs();

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((true, 17590m), (domain.DnsManagement, domain.RecurringAmount));
        Assert.AreEqual(1, await h.Query(db => db.DomainOperations.CountAsync(o => o.Operation == DomainJobs.AddOption)));
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
    }

    [TestMethod]
    public async Task Transferencia_SinRegistrarQueLaHaga_NoSeFactura()
    {
        await using var s = await Start(capabilities: ["registrar.pricing"]);
        var h = s.H;

        var error = await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-CORRECTO"))));

        StringAssert.Contains(error.Message, "transferencias");
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
    }

    [TestMethod]
    public async Task Transferencia_FallaPorElCodigo_SeReintentaConOtro_YTermina()
    {
        await using var s = await Start();
        var h = s.H;
        s.Fake.TransfersPending = false;
        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-MALO")));
        await Pay(h, 14000m, "T-4");
        await h.RunJobs();

        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(placed.DomainId)));
        await Run<DomainService>(h, d => d.Retry(placed.DomainId, "EPP-CORRECTO"));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Retry(placed.DomainId)), "Con un reintento en curso no se pide otro.");
        await h.RunJobs();

        Assert.AreEqual("EPP-CORRECTO", s.Fake.Calls.Single(c => c.Operation == "transfer").Input["auth_code"]!.ToString());
        var ops = await h.Query(db => db.DomainOperations.OrderBy(o => o.Id).Select(o => o.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { DomainOperationStatus.Failed, DomainOperationStatus.Completed }, ops);
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual(DomainStatus.Active, domain.Status);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()), "Reintentar no vuelve a facturar.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Retry(placed.DomainId)));
    }

    [TestMethod]
    public async Task Avisos_RegistroTransferenciaYRenovacionTerminados_YLaTransferenciaFallidaConSuMotivo()
    {
        await using var s = await Start();
        var h = s.H;
        s.Fake.TransfersPending = false;
        var registered = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com")));
        var transfer = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-MALO")));

        await Pay(h, 15000m, "T-20", registered.InvoiceId);
        await Pay(h, 14000m, "T-21", transfer.InvoiceId);
        await h.RunJobs();
        await Run<DomainService>(h, d => d.SetLock(registered.DomainId, true));
        await Run<DomainService>(h, d => d.Retry(transfer.DomainId, "EPP-CORRECTO"));
        await h.RunJobs();
        var renewal = await h.Use<DomainService, long?>(d => d.RenewNow(registered.DomainId));
        await Pay(h, 16000m, "T-22", renewal);
        await h.RunJobs();

        var sent = h.DomainMail.Sent.Values.OrderBy(v => v.Notice).Select(v => (v.Notice, v.Data.Domain, v.Data.ExpiresOn)).ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                (DomainNotice.Registered, "nuevo.com", new DateOnly(2027, 11, 2)),
                (DomainNotice.Transferred, "traer.com", s.Fake.Domains["traer.com"].Expires),
                (DomainNotice.Renewed, "nuevo.com", new DateOnly(2028, 11, 2)),
                (DomainNotice.TransferFailed, "traer.com", new DateOnly(2026, 11, 2)),
            },
            sent);
        Assert.AreEqual("invalid_auth_code", h.DomainMail.Sent.Values.Single(v => v.Notice == DomainNotice.TransferFailed).Data.Reason);
        Assert.AreEqual(4, h.DomainMail.Requests, "El bloqueo no avisa y cada aviso se pide una vez.");
        Assert.IsTrue(h.DomainMail.Sent.Keys.All(k => k.StartsWith("domain-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancelarPedido_FallidoYPago_AnulaLaFacturaYLoPagadoQuedaComoSaldo()
    {
        await using var s = await Start();
        var h = s.H;
        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-MALO")));
        await Pay(h, 14000m, "T-5");
        await h.RunJobs();

        await Run<DomainService>(h, d => d.CancelOrder(placed.DomainId, "El cliente no consiguió el código"));
        await Run<DomainService>(h, d => d.CancelOrder(placed.DomainId, "Otra vez"));
        await h.RunJobs();

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Cancelled, false), (domain.Status, domain.AutoRenew));
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(InvoiceStatus.Cancelled, invoice.Status);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote)), "Repetir la cancelación no emite otra nota.");
        Assert.AreEqual(14000m, (await h.Query(db => db.CreditBalances.SingleAsync(b => b.AccountId == h.AccountId))).Balance);
        Assert.AreEqual(1, await h.Query(db => db.DomainOperations.CountAsync()), "Cancelar no vuelve a llamar al registrar.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Retry(placed.DomainId, "EPP-CORRECTO")));
    }

    [TestMethod]
    public async Task CancelarPedido_SinPagar_LaOperacionNoSaleNunca_YEnCursoNoSePuede()
    {
        await using var s = await Start();
        var h = s.H;
        var unpaid = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com")));
        var inFlight = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Transfer(h.AccountId, new DomainOrder("traer.com", AuthCode: "EPP-CORRECTO")));
        await Pay(h, 14000m, "T-6", inFlight.InvoiceId);
        await h.RunJobs();

        await Run<DomainService>(h, d => d.CancelOrder(unpaid.DomainId, "Ya no lo quiere"));
        var error = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.CancelOrder(inFlight.DomainId, "Apurado")));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.CancelOrder(inFlight.DomainId, " ")));
        await Pay(h, 15000m, "T-7");
        await h.RunJobs();

        StringAssert.Contains(error.Message, "en curso");
        Assert.IsFalse(s.Fake.Calls.Any(c => c.Operation == "register"), "Un pedido cancelado nunca va al registrar.");
        Assert.AreEqual(InvoiceStatus.Cancelled, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == unpaid.InvoiceId))).Status);
        Assert.AreEqual(DomainOperationStatus.Failed, (await h.Query(db => db.DomainOperations.SingleAsync(o => o.DomainId == unpaid.DomainId))).Status);
        Assert.AreEqual(DomainStatus.Pending, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == inFlight.DomainId))).Status);
    }

    [TestMethod]
    public async Task RenovacionEnLaCorrida_AlPagarseRenuevaEnElRegistrar_YElVencimientoSaleDeSuRespuesta()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));

        await h.Use<BillingRunService, int>(r => r.Run());
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync());
        await Pay(h, 16000m, "T-8");
        await h.RunJobs();
        await h.Use<BillingRunService, int>(r => r.Run());

        Assert.AreEqual((16000m, LineKind.Renewal, id), (invoice.Total, invoice.Lines.Single().Kind, invoice.Lines.Single().DomainId));
        var renew = s.Fake.Calls.Single(c => c.Operation == "renew");
        Assert.AreEqual(("2026-11-07", 1), (renew.Input["current_expiry"]!.ToString(), renew.Input["years"]!.GetValue<int>()));
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((new DateOnly(2027, 11, 7), new DateOnly(2027, 11, 7), new DateOnly(2027, 11, 7)), (domain.ExpiresOn, domain.PaidThrough, domain.NextDueDate));
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()), "La corrida siguiente no factura otra vez.");
    }

    [TestMethod]
    public async Task Vencido_QuePagaTarde_SeLePideLaRenovacionAlRegistrar_YSiLaHaceVuelveAActivo()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        h.Clock.Now = new DateTimeOffset(2026, 12, 23, 15, 0, 0, TimeSpan.Zero); // Pasó la gracia de 45 días sin pagar.
        await h.Use<BillingRunService, int>(r => r.Run());
        Assert.AreEqual(DomainStatus.Expired, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == id))).Status);

        await Pay(h, 16000m, "T-50");
        await h.RunJobs();

        Assert.AreEqual("2026-11-07", s.Fake.Calls.Single(c => c.Operation == "renew").Input["current_expiry"]!.ToString());
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == id));
        Assert.AreEqual((DomainStatus.Active, new DateOnly(2027, 11, 7), new DateOnly(2027, 11, 7)), (domain.Status, domain.ExpiresOn, domain.PaidThrough), "El registro la aceptó: vuelve a activo con el vencimiento nuevo.");
        Assert.AreEqual(DomainNotice.Renewed, h.DomainMail.Sent.Values.Single().Notice);
        await h.Use<BillingRunService, int>(r => r.Run());
        Assert.AreEqual(DomainStatus.Active, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == id))).Status);
    }

    [TestMethod]
    public async Task DosRenovacionesPagasJuntas_AvanzanLoPagoHastaLoFacturado_YPidenUnaRenovacion()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        var extra = await h.DomainInvoice(id, LineKind.Renewal, 16000m, "Renovación repetida a mano");

        await Pay(h, 32000m, "T-53");
        await h.RunJobs();

        Assert.IsTrue(await h.Query(db => db.Documents.Where(d => d.Kind == DocumentKind.Invoice).AllAsync(d => d.Allocated == d.Total)), "Las dos facturas quedaron pagas.");
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == id));
        Assert.AreEqual((new DateOnly(2027, 11, 7), new DateOnly(2027, 11, 7)), (domain.PaidThrough, domain.NextDueDate), "Lo pago no pasa lo facturado por la corrida.");
        Assert.AreEqual(1, await h.Query(db => db.DomainOperations.CountAsync(o => o.Operation == "renew")));
        Assert.IsTrue(extra > 0);
    }

    [TestMethod]
    public async Task PagoDeUnaRenovacion_EsperaLaFilaDelDominio_YAvanzaDesdeLoQueDejoOtroNodo()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        await Pay(h, 16000m, "T-56");

        // Otro nodo tiene tomada la fila: pagó otra renovación ya facturada del mismo dominio y todavía no confirmó. Se suelta
        // recién cuando el trabajo del pago espera un bloqueo: sin el de la fila, ya leyó lo pago viejo y espera para escribir.
        await h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE domains.domains SET paid_through = '2027-11-07', next_due_date = '2028-11-07' WHERE id = {id}");
            var jobs = h.RunJobs();
            for (var i = 0; i < 600 && !jobs.IsCompleted && !await WaitingForLock(db); i++)
            {
                await Task.Delay(100);
            }

            Assert.IsFalse(jobs.IsCompleted, "El pago espera a que el otro nodo suelte el dominio.");
            await tx.CommitAsync();
            await jobs;
        });

        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == id));
        Assert.AreEqual(new DateOnly(2028, 11, 7), domain.PaidThrough, "Avanza desde lo que dejó el otro nodo, sin pisarlo.");
        Assert.AreEqual("2027-11-07", s.Fake.Calls.Single(c => c.Operation == "renew").Input["current_expiry"]!.ToString());
    }

    /// <summary>Otra conexión de la base de la prueba espera un bloqueo.</summary>
    private static async Task<bool> WaitingForLock(BillingDbContext db) =>
        await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock'").SingleAsync() > 0;

    [TestMethod]
    public async Task RenovacionAnuladaConNotaDeCredito_VuelveAEstarPorFacturar_PorRenovarAhoraOPorLaCorrida()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        var first = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync());
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(id)), "Facturada sin pagar, no se suma otra.");

        await Run<CreditService>(h, c => c.IssueCreditNote(first.Id, Lines(first), "Se refactura"));
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 7)), (domain.NextDueDate, domain.PaidThrough), "Anulada, el período vuelve a estar por facturar.");
        Assert.AreEqual($"domain:{id}:2026-11-07", await h.Query(db => db.DocumentLines.Where(l => l.DocumentId == first.Id).Select(l => l.DedupKey).SingleAsync()), "El renglón emitido no cambia: está congelado.");

        // Renovar ahora la vuelve a emitir con la clave siguiente del período, y anulada otra vez (por ejemplo por la mora), la corrida también.
        var second = (await h.Use<DomainService, long?>(d => d.RenewNow(id)))!.Value;
        var secondInvoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == second));
        await Run<CreditService>(h, c => c.IssueCreditNote(second, Lines(secondInvoice), "Impaga"));
        await h.Use<BillingRunService, int>(r => r.Run());
        var third = await h.Query(db => db.Documents.Include(d => d.Lines).Where(d => d.Kind == DocumentKind.Invoice).OrderByDescending(d => d.Id).FirstAsync());

        Assert.AreNotEqual(second, third.Id);
        Assert.AreEqual($"domain:{id}:2026-11-07:1", secondInvoice.Lines.Single().DedupKey);
        Assert.AreEqual((first.Lines.Single().Description, $"domain:{id}:2026-11-07:2"), (third.Lines.Single().Description, third.Lines.Single().DedupKey), "El mismo período, con la clave siguiente.");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(id)), "Facturada de nuevo, Renovar ahora no suma otra.");
        await Pay(h, 16000m, "T-52", third.Id);
        await h.RunJobs();
        Assert.AreEqual(new DateOnly(2027, 11, 7), (await h.Query(db => db.Domains.SingleAsync())).ExpiresOn);
        Assert.AreEqual(1, s.Fake.Calls.Count(c => c.Operation == "renew"));
    }

    private static List<CreditLine> Lines(Document invoice) =>
        [.. invoice.Lines.Select(l => new CreditLine(l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, l.ServiceId, DomainId: l.DomainId))];

    private static Task<int> Dunning(CoreHarness h) => h.Use<DunningService, int>(d => d.Run());

    private static void At(CoreHarness h, int year, int month, int day) => h.Clock.Now = new DateTimeOffset(year, month, day, 15, 0, 0, TimeSpan.Zero);

    private static Task<Document> Invoice(CoreHarness h, long id) => h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == id));

    /// <summary>Una factura de la cuenta con el renglón de renovación del dominio y otro renglón, como el borrador compartido de la corrida.</summary>
    private static Task<long> SharedInvoice(CoreHarness h, long domainId, string description, decimal amount, string other, decimal otherAmount, string? key = null) => h.Scope(async sp =>
    {
        var db = sp.GetRequiredService<BillingDbContext>();
        var core = sp.GetRequiredService<BillingCore>();
        var receiver = await core.Receiver(h.AccountId, CancellationToken.None);
        var invoice = new Document(DocumentKind.Invoice, h.AccountId, Currency.Ars, PriceMode.TaxIncluded, core.Today().AddDays(core.Options.InvoiceDueDays), h.Clock.Now, reason: "renewal");
        invoice.AddLine(LineKind.Renewal, description, 1, amount, TaxTreatment.Taxed, 0.21m, dedupKey: key, domainId: domainId);
        invoice.AddLine(LineKind.OneTime, other, 1, otherAmount, TaxTreatment.Taxed, 0.21m);
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Documents.Add(invoice);
        await core.IssueDraft(invoice, receiver, CancellationToken.None);
        await core.Commit(tx, CancellationToken.None);
        return invoice.Id;
    });

    [TestMethod]
    public async Task AnuladaPorLaMora_NoSeReemiteSola_YRenovarAhoraLaVuelveAFacturarConVencimientoFuturo()
    {
        await using var s = await Start(o => o.CancelUnpaidAfterDays = 7);
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        var first = await h.Query(db => db.Documents.SingleAsync());

        // Vencida hace más de siete días: la mora la anula, y las corridas y moras siguientes no la vuelven a emitir.
        for (var day = 15; day <= 17; day++)
        {
            At(h, 2026, 11, day);
            await h.Use<BillingRunService, int>(r => r.Run());
            await Dunning(h);
            await h.RunJobs();
        }

        Assert.AreEqual((InvoiceStatus.Cancelled, 1, 1), ((await Invoice(h, first.Id)).Status, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.Invoice)), await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote))));
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Grace, new DateOnly(2027, 11, 7), new DateOnly(2026, 11, 7)), (domain.Status, domain.NextDueDate, domain.PaidThrough), "El período queda facturado sin factura abierta: lo decide el cliente o el staff.");
        Assert.IsTrue(await h.Use<DomainService, bool>(d => d.CanRenewNow(domain)), "Sin factura abierta, Renovar ahora se ofrece.");

        // Renovar ahora vuelve a facturar el mismo período con la clave siguiente y vence en el futuro, y pagarla lo renueva.
        var again = (await h.Use<DomainService, long?>(d => d.RenewNow(id)))!.Value;
        var reissued = await Invoice(h, again);
        Assert.AreEqual((new DateOnly(2026, 11, 24), 16000m, $"domain:{id}:2026-11-07:1"), (reissued.DueDate, reissued.Total, reissued.Lines.Single().DedupKey));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(id)), "Facturada de nuevo, no se suma otra.");
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(r => r.Run()));
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Issued, (await Invoice(h, again)).Status, "La factura nueva no venció: la mora no la toca.");

        await Pay(h, 16000m, "T-60", again);
        await h.RunJobs();
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Active, new DateOnly(2027, 11, 7), new DateOnly(2027, 11, 7)), (domain.Status, domain.PaidThrough, domain.ExpiresOn));
        Assert.AreEqual("2026-11-07", s.Fake.Calls.Single(c => c.Operation == "renew").Input["current_expiry"]!.ToString());
    }

    [TestMethod]
    public async Task RenovacionSinClaveDePeriodo_AnuladaPorElStaffLaCorridaLaReemite_YAnuladaPorLaMoraLaVuelveAFacturarRenovarAhora()
    {
        await using var s = await Start(o => o.CancelUnpaidAfterDays = 7);
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));

        // Como lo deja el importador o una factura hecha a mano: el período facturado y el renglón sin clave.
        await h.Db(db => db.Database.ExecuteSqlAsync($"UPDATE domains.domains SET next_due_date = '2027-11-07' WHERE id = {id}"));
        var imported = await h.DomainInvoice(id, LineKind.Renewal, 15000m, "Renovación del dominio ejemplo.com, 1 año (07/11/2026 al 06/11/2027)");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(id)), "Facturada sin pagar, no se suma otra.");

        // El staff la anula para refacturar: el período vuelve a estar por facturar y la corrida lo emite con su clave.
        var importedLines = Lines(await Invoice(h, imported));
        await Run<CreditService>(h, c => c.IssueCreditNote(imported, importedLines, "Se refactura"));
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 7)), (domain.NextDueDate, domain.PaidThrough));
        Assert.AreEqual(1, await h.Use<BillingRunService, int>(r => r.Run()));
        var byRun = await h.Query(db => db.Documents.Include(d => d.Lines).Where(d => d.Kind == DocumentKind.Invoice).OrderByDescending(d => d.Id).FirstAsync());
        Assert.AreEqual(($"domain:{id}:2026-11-07", 16000m, new DateOnly(2026, 11, 7)), (byRun.Lines.Single().DedupKey, byRun.Total, byRun.DueDate), "Con el precio congelado del dominio: no hay un renglón anterior del período con clave.");

        // La mora anula esa: el dominio no queda trabado, Renovar ahora la vuelve a emitir y la corrida no.
        At(h, 2026, 11, 20);
        await Dunning(h);
        Assert.AreEqual(InvoiceStatus.Cancelled, (await Invoice(h, byRun.Id)).Status);
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(r => r.Run()));
        var again = (await h.Use<DomainService, long?>(d => d.RenewNow(id)))!.Value;
        var reissued = await Invoice(h, again);
        Assert.AreEqual(($"domain:{id}:2026-11-07:1", 16000m, new DateOnly(2026, 11, 27)), (reissued.Lines.Single().DedupKey, reissued.Total, reissued.DueDate));
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Grace, new DateOnly(2027, 11, 7), new DateOnly(2026, 11, 7)), (domain.Status, domain.NextDueDate, domain.PaidThrough));
    }

    [TestMethod]
    public async Task RenovacionSinClaveDeUnPeriodoAnterior_ReintegradaEntera_NoLiberaElPeriodoQueYaEstaFacturado()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));

        // Una renovación importada del año pasado, ya paga, y la del período que sigue facturada por la corrida, sin pagar.
        var old = await h.DomainInvoice(id, LineKind.Renewal, 15000m, "Renovación del dominio ejemplo.com, 1 año (07/11/2025 al 06/11/2026)");
        await Pay(h, 15000m, "T-67", old);
        await h.RunJobs();
        Assert.AreEqual(1, await h.Use<BillingRunService, int>(r => r.Run()));

        // El staff reintegra la vieja entera: no era la del período facturado, que sigue facturado y no se repite.
        var oldLines = Lines(await Invoice(h, old));
        await Run<CreditService>(h, c => c.IssueCreditNote(old, oldLines, "Reintegro"));
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((new DateOnly(2027, 11, 7), new DateOnly(2026, 11, 7)), (domain.NextDueDate, domain.PaidThrough));
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(r => r.Run()));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(id)), "Facturada sin pagar, no se suma otra.");
    }

    [TestMethod]
    public async Task OpcionContratadaConLaRenovacionFacturada_LaReemisionCopiaElRenglonAnulado_YLaOpcionSeCobraRecienEnElPeriodoSiguiente()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await Run<TldPricing>(h, p => p.SetOptionPrice(DomainOptions.Dns, Currency.Ars, 3650m, new DateOnly(2026, 1, 1)));
        await h.Use<BillingRunService, int>(r => r.Run());
        var renewal = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync());

        // Con la renovación facturada sin pagar, la opción se prorratea hasta el fin de ese período y se paga.
        var optionInvoice = (await h.Use<DomainService, long?>(d => d.SetOption(id, DomainOptions.Dns, true)))!.Value;
        await Pay(h, (await Invoice(h, optionInvoice)).Total, "T-61", optionInvoice);
        await h.RunJobs();
        var contracted = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((true, 19650m), (contracted.DnsManagement, contracted.RecurringAmount));

        // Anulada y reemitida, la factura nueva del período lleva el renglón anulado tal cual: la opción ya está paga hasta su fin.
        await Run<CreditService>(h, c => c.IssueCreditNote(renewal.Id, Lines(renewal), "Se refactura"));
        var reissued = await Invoice(h, (await h.Use<DomainService, long?>(d => d.RenewNow(id)))!.Value);
        Assert.AreEqual((16000m, renewal.Lines.Single().Description, $"domain:{id}:2026-11-07:1"), (reissued.Total, reissued.Lines.Single().Description, reissued.Lines.Single().DedupKey));

        // El período siguiente sí la lleva, con el precio congelado en el dominio.
        await Pay(h, 16000m, "T-62", reissued.Id);
        await h.RunJobs();
        var next = await Invoice(h, (await h.Use<DomainService, long?>(d => d.RenewNow(id)))!.Value);
        Assert.AreEqual((19650m, "Renovación del dominio ejemplo.com, 1 año (07/11/2027 al 06/11/2028) + gestión de DNS"), (next.Total, next.Lines.Single().Description));
    }

    [TestMethod]
    public async Task RenglonDelDominioAcreditado_AlPagarseElResto_NoRenueva_YElPeriodoVuelveAEstarPorFacturar()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        await h.Db(db => db.Database.ExecuteSqlAsync($"UPDATE domains.domains SET next_due_date = '2027-11-07' WHERE id = {id}"));
        var invoiceId = await SharedInvoice(h, id, "Renovación del dominio ejemplo.com, 1 año (07/11/2026 al 06/11/2027)", 16000m, "Instalación del sitio", 5000m, $"domain:{id}:2026-11-07");

        // El cliente no renueva el dominio: el staff acredita su renglón con el texto de la factura, sin decir el dominio.
        var line = (await Invoice(h, invoiceId)).Lines.Single(l => l.DomainId == id);
        await Run<CreditService>(h, c => c.IssueCreditNote(invoiceId, [new CreditLine(line.Description, line.Quantity, line.UnitPrice, line.Treatment, line.Rate)], "El cliente no renueva el dominio"));
        Assert.AreEqual(id, await h.Query(db => db.DocumentLines.Where(l => db.Documents.Any(n => n.Id == l.DocumentId && n.Kind == DocumentKind.CreditNote)).Select(l => l.DomainId).SingleAsync()), "La nota lleva el dominio de su renglón.");
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 7)), (domain.NextDueDate, domain.PaidThrough), "Acreditado entero, el período vuelve a estar por facturar.");

        // Libre otra vez, la corrida la factura de nuevo con la clave siguiente (si no se quería, se apaga la renovación).
        Assert.AreEqual(1, await h.Use<BillingRunService, int>(r => r.Run()));
        Assert.AreEqual($"domain:{id}:2026-11-07:1", await h.Query(db => db.DocumentLines.Where(l => l.DomainId == id).OrderByDescending(l => l.Id).Select(l => l.DedupKey).FirstAsync()));

        // El cliente paga el resto de la compartida: la renovación acreditada no la pagó nadie, aunque esté facturada de nuevo.
        await Pay(h, 5000m, "T-63", invoiceId);
        await h.RunJobs();
        var invoice = await Invoice(h, invoiceId);
        Assert.AreEqual((InvoiceStatus.Paid, 16000m, 5000m), (invoice.Status, invoice.Credited, invoice.Allocated));
        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((new DateOnly(2026, 11, 7), new DateOnly(2027, 11, 7), new DateOnly(2026, 11, 7)), (domain.PaidThrough, domain.NextDueDate, domain.ExpiresOn), "Nadie pagó la renovación: lo pago no avanza.");
        Assert.AreEqual(0, s.Fake.Calls.Count(c => c.Operation == "renew"));
        Assert.AreEqual(0, await h.Query(db => db.DomainOperations.CountAsync()));
    }

    [TestMethod]
    public async Task PedidoImpagoAnuladoPorLaMora_SeCierra_YElNombreSePuedeVolverAPedir()
    {
        await using var s = await Start(o => o.CancelUnpaidAfterDays = 7);
        var h = s.H;
        var placed = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com")));

        At(h, 2026, 11, 20);
        await Dunning(h);
        await h.RunJobs();

        Assert.AreEqual(InvoiceStatus.Cancelled, (await Invoice(h, placed.InvoiceId!.Value)).Status);
        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == placed.DomainId));
        Assert.AreEqual((DomainStatus.Cancelled, false), (domain.Status, domain.AutoRenew), "El pedido se cierra como al cancelarlo: el nombre se libera.");
        var op = await h.Query(db => db.DomainOperations.SingleAsync(o => o.DomainId == placed.DomainId));
        Assert.AreEqual(DomainOperationStatus.Failed, op.Status);
        StringAssert.Contains(op.LastError, DunningService.CancelReason);
        Assert.IsFalse(s.Fake.Calls.Any(c => c.Operation == "register"));

        var again = await h.Use<DomainOrderService, PlacedDomainOrder>(o => o.Register(h.AccountId, new DomainOrder("nuevo.com")));
        Assert.AreNotEqual(placed.DomainId, again.DomainId);
        await Pay(h, 15000m, "T-64", again.InvoiceId);
        await h.RunJobs();
        Assert.AreEqual(DomainStatus.Active, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == again.DomainId))).Status);
        Assert.AreEqual(1, s.Fake.Calls.Count(c => c.Operation == "register"));
    }

    [TestMethod]
    public async Task DominioQueSeFue_LaSincronizacionAnulaSuRenovacionAbierta_SoloSuRenglon_YNoTocaLaYaPaga()
    {
        await using var s = await Start();
        var h = s.H;
        var gone = await Active(s, "se-va.com", new DateOnly(2026, 11, 7));
        var stays = await Active(s, "se-queda.com", new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        var shared = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync());
        Assert.AreEqual(2, shared.Lines.Count, "Las dos renovaciones de la cuenta van en la misma factura.");

        s.Fake.Domains["se-va.com"].Status = "transferred_away";
        await Run<DomainService>(h, d => d.SyncNow(gone));
        await h.RunJobs();

        var closed = await h.Query(db => db.Domains.SingleAsync(d => d.Id == gone));
        Assert.AreEqual((DomainStatus.Cancelled, "transferred_away"), (closed.Status, closed.RegistryStatus));
        var invoice = await Invoice(h, shared.Id);
        Assert.AreEqual((InvoiceStatus.Issued, 16000m, 16000m), (invoice.Status, invoice.Credited, invoice.Pending), "Solo el renglón del dominio que se fue se acredita: el otro se sigue debiendo.");
        var note = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Kind == DocumentKind.CreditNote));
        Assert.AreEqual((shared.Id, gone), (note.RelatedDocumentId, note.Lines.Single().DomainId));
        Assert.AreEqual(new DateOnly(2027, 11, 7), (await h.Query(db => db.Domains.SingleAsync(d => d.Id == stays))).NextDueDate, "El otro dominio sigue facturado.");

        await Pay(h, 16000m, "T-65", shared.Id);
        await h.RunJobs();
        Assert.AreEqual("se-queda.com", s.Fake.Calls.Single(c => c.Operation == "renew").Context["domain"]!["name"]!.ToString());
        Assert.AreEqual(new DateOnly(2027, 11, 7), (await h.Query(db => db.Domains.SingleAsync(d => d.Id == stays))).PaidThrough);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Where(b => b.AccountId == h.AccountId).Select(b => b.Balance).SingleOrDefaultAsync()));

        // Una renovación ya paga y hecha en el registro no se anula sola: la decide el staff.
        var paid = await Active(s, "pago.com", new DateOnly(2026, 11, 7));
        await h.Use<BillingRunService, int>(r => r.Run());
        var paidInvoice = await h.Query(db => db.Documents.Where(d => d.Kind == DocumentKind.Invoice).OrderByDescending(d => d.Id).FirstAsync());
        await Pay(h, 16000m, "T-66", paidInvoice.Id);
        await h.RunJobs();
        s.Fake.Domains["pago.com"].Status = "transferred_away";
        await Run<DomainService>(h, d => d.SyncNow(paid));
        await h.RunJobs();
        Assert.AreEqual(DomainStatus.Cancelled, (await h.Query(db => db.Domains.SingleAsync(d => d.Id == paid))).Status);
        Assert.AreEqual((InvoiceStatus.Paid, 1), ((await Invoice(h, paidInvoice.Id)).Status, await h.Query(db => db.Documents.CountAsync(d => d.Kind == DocumentKind.CreditNote))));
    }

    [TestMethod]
    public async Task RenovacionFallidaEnElRegistrar_SeReintentaComoIntencionNueva_SoloMientrasNoSeRepitio()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        s.Fake.RenewsFail = true;
        await h.Use<BillingRunService, int>(r => r.Run());
        await Pay(h, 16000m, "T-54");
        await h.RunJobs();

        var failed = await h.Query(db => db.DomainOperations.SingleAsync());
        Assert.AreEqual((DomainOperationStatus.Failed, "renew"), (failed.Status, failed.Operation));
        StringAssert.Contains(failed.LastError, "rejected");
        Assert.AreEqual(new DateOnly(2027, 11, 7), (await h.Query(db => db.Domains.SingleAsync())).PaidThrough, "Lo pago avanzó con el pago: el registro quedó sin renovar.");
        var code = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Retry(id, "EPP-CORRECTO")));
        StringAssert.Contains(code.Message, "transferencia");

        s.Fake.RenewsFail = false;
        await Run<DomainService>(h, d => d.Retry(id));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Retry(id)), "Con el reintento en la cola no se pide otro.");
        await h.RunJobs();

        var statuses = await h.Query(db => db.DomainOperations.OrderBy(o => o.Id).Select(o => o.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { DomainOperationStatus.Failed, DomainOperationStatus.Completed }, statuses);
        Assert.AreEqual("2026-11-07", s.Fake.Calls.Single(c => c.Operation == "renew").Input["current_expiry"]!.ToString());
        Assert.AreEqual(new DateOnly(2027, 11, 7), (await h.Query(db => db.Domains.SingleAsync())).ExpiresOn);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()), "Reintentar no vuelve a facturar.");
        var done = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.Retry(id)));
        StringAssert.Contains(done.Message, "no tiene una operación fallida");
    }

    [TestMethod]
    public async Task Busqueda_ConElRegistroColgado_LasConsultasVanEnParaleloYCortanEnElPresupuesto()
    {
        var rdap = new FakeRdap { Hang = true };
        await using var s = await Start(o =>
        {
            o.DomainLookup.Enabled = true;
            o.DomainLookup.BootstrapUrl = FakeRdap.Bootstrap;
            o.DomainLookup.SearchBudgetSeconds = 1;
            o.DomainLookup.DnsResolvers = ["127.0.0.1:9"];
        }, rdap);
        var h = s.H;
        await h.Db(async db =>
        {
            var nicAr = await ManualModule.AddInstance(db, "nic-ar-manual", new JsonObject { ["queue"] = "dominios", ["registry"] = "nic.ar" }, h.Clock.Now);
            db.TldPrices.Add(new TldPrice(".com.ar", nicAr, Currency.Ars, 1, new DateOnly(2026, 1, 1)) { Register = 9000m, Renew = 9000m });
            await db.SaveChangesAsync();
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = await h.Use<DomainSearch, IReadOnlyList<DomainAvailability>>(d => d.Check(["uno.com.ar", "dos.com.ar", "tres.com.ar", "libre.com"], Currency.Ars));
        watch.Stop();

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(12), $"Tardó {watch.Elapsed}: sin presupuesto cada consulta espera sus tres intentos de cinco segundos, una tras otra.");
        Assert.IsTrue(results.Where(r => r.Name.EndsWith(".com.ar", StringComparison.Ordinal)).All(r => r.Available is null && r.Reason == "rdap_unavailable"), "Sin respuesta queda sin confirmar, nunca libre.");
        Assert.AreEqual(true, results.Single(r => r.Name == "libre.com").Available, "El registrar contesta los suyos igual.");
        Assert.IsTrue(rdap.Requests >= 3, "Las tres consultas salieron a la vez.");
    }

    [TestMethod]
    public async Task RenovarAhora_FacturaElProximoPeriodoUnaVez_YLaCorridaNoLoRepite()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2027, 6, 1));
        await Run<DomainService>(h, d => d.SetAutoRenew(id, false));

        var invoiceId = await h.Use<DomainService, long?>(d => d.RenewNow(id));
        var twice = await Assert.ThrowsExactlyAsync<ValidationException>(() => Run<DomainService>(h, d => d.RenewNow(id)));
        h.Clock.Now = new DateTimeOffset(2027, 5, 30, 15, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(r => r.Run());

        StringAssert.Contains(twice.Message, "falta pagarla");
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync());
        Assert.AreEqual((invoiceId, 16000m), ((long?)invoice.Id, invoice.Total));
        Assert.AreEqual("Renovación del dominio ejemplo.com, 1 año (01/06/2027 al 31/05/2028)", invoice.Lines.Single().Description);
        await Pay(h, 16000m, "T-9");
        await h.RunJobs();
        Assert.AreEqual(new DateOnly(2028, 6, 1), (await h.Query(db => db.Domains.SingleAsync())).ExpiresOn);
    }

    [TestMethod]
    public async Task EnGracia_ConLaRenovacionPrendidaDeNuevo_LaCorridaLaFactura_YAlPagarseVuelveAActivo()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 10, 25));
        await Run<DomainService>(h, d => d.SetAutoRenew(id, false));
        await h.Use<BillingRunService, int>(r => r.Run());
        Assert.AreEqual((DomainStatus.Grace, 0), ((await h.Query(db => db.Domains.SingleAsync())).Status, await h.Query(db => db.Documents.CountAsync())));

        await Run<DomainService>(h, d => d.SetAutoRenew(id, true));
        await h.Use<BillingRunService, int>(r => r.Run());
        Assert.AreEqual(new DateOnly(2026, 11, 9), (await h.Query(db => db.Documents.SingleAsync())).DueDate, "El período ya empezó: la factura vence a los días de la emisión, nunca en el pasado.");
        await Pay(h, 16000m, "T-10");
        await h.RunJobs();

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Active, new DateOnly(2027, 10, 25)), (domain.Status, domain.ExpiresOn));
    }

    [TestMethod]
    public async Task NoRenovar_DejaDeFacturarSinDarDeBaja()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));

        await Run<DomainService>(h, d => d.SetAutoRenew(id, false));
        await h.Use<BillingRunService, int>(r => r.Run());

        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
        Assert.AreEqual(DomainStatus.Active, (await h.Query(db => db.Domains.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Precios_ElVigenteEsElDeFechaMasReciente_YLaMismaFechaCorrige()
    {
        await using var s = await Start();
        var h = s.H;

        await Run<TldPricing>(h, p => p.Set("COM", s.Module.InstanceId, Currency.Ars, 1, 17000m, 18000m, 16000m, new DateOnly(2026, 10, 1)));
        await Run<TldPricing>(h, p => p.Set(".com", s.Module.InstanceId, Currency.Ars, 1, 17500m, 18000m, 16000m, new DateOnly(2026, 10, 1)));
        await Run<TldPricing>(h, p => p.Set(".com", s.Module.InstanceId, Currency.Ars, 1, 99999m, 99999m, 99999m, new DateOnly(2027, 1, 1)));

        var offer = await h.Use<TldPricing, TldOffer?>(p => p.Offer(".com", Currency.Ars, 1));
        Assert.AreEqual((17500m, 18000m, new DateOnly(2026, 10, 1)), (offer!.Register, offer.Renew, offer.ValidFrom));
        Assert.AreEqual(4, await h.Query(db => db.TldPrices.CountAsync()));
        Assert.IsNull(await h.Use<TldPricing, TldOffer?>(p => p.Offer(".com", Currency.Usd, 1)));
    }

    [TestMethod]
    public async Task Precios_ImportarDelRegistrar_PrimeroMuestraYDespuesGuardaConLaRegla()
    {
        await using var s = await Start();
        var h = s.H;
        var rule = new PriceRule(Currency.Ars, ExchangeRate: 1000m, MarginPercent: 30m, TaxPercent: 21m, RoundTo: 100m);

        var preview = await h.Use<TldPricing, IReadOnlyList<ImportedTldPrice>>(p => p.ImportFromRegistrar(s.Module.InstanceId, rule, new DateOnly(2026, 11, 2), apply: false));
        Assert.AreEqual(2, await h.Query(db => db.TldPrices.CountAsync()), "La vista previa no guarda nada.");
        await h.Use<TldPricing, IReadOnlyList<ImportedTldPrice>>(p => p.ImportFromRegistrar(s.Module.InstanceId, rule, new DateOnly(2026, 11, 2), apply: true));

        var com = preview.Single(p => p.Tld == ".com");
        Assert.AreEqual(("USD", 9.50m, 15000m, 16200m, 15400m), (com.CostCurrency, com.CostRegister!.Value, com.Register!.Value, com.Renew!.Value, com.Transfer!.Value));
        Assert.IsNull(preview.Single(p => p.Tld == ".net").Renew);
        var offer = await h.Use<TldPricing, TldOffer?>(p => p.Offer(".com", Currency.Ars, 1));
        Assert.AreEqual((15000m, new DateOnly(2026, 11, 2)), (offer!.Register, offer.ValidFrom));
        Assert.AreEqual(17400m, (await h.Use<TldPricing, TldOffer?>(p => p.Offer(".net", Currency.Ars, 1)))!.Register);
    }

    [TestMethod]
    public async Task Precios_DesdeUnCsvConPuntoYComaYComaDecimal_PrimeroMuestraYDespuesGuarda()
    {
        await using var s = await Start();
        var h = s.H;
        const string csv = "tld;years;register;renew;transfer\r\nCOM.AR;1;9.000,50;9000;\n.net;2;30000;;31000\n";
        Task<IReadOnlyList<ImportedTldPrice>> Import(string text, bool apply = false) =>
            h.Use<TldPricing, IReadOnlyList<ImportedTldPrice>>(p => p.ImportCsv(s.Module.InstanceId, Currency.Ars, text, new DateOnly(2026, 11, 2), apply));

        var preview = await Import(csv);
        Assert.AreEqual(2, await h.Query(db => db.TldPrices.CountAsync()), "La propuesta no guarda nada.");
        await Import(csv, apply: true);

        var comAr = preview.Single(p => p.Tld == ".com.ar");
        Assert.AreEqual((1, 9000.50m, 9000m, (decimal?)null), (comAr.Years, comAr.Register, comAr.Renew, comAr.Transfer));
        Assert.AreEqual((2, 31000m, (decimal?)null), (preview.Single(p => p.Tld == ".net").Years, preview.Single(p => p.Tld == ".net").Transfer!.Value, preview.Single(p => p.Tld == ".net").Renew));
        Assert.AreEqual(9000.50m, (await h.Use<TldPricing, TldOffer?>(p => p.Offer(".com.ar", Currency.Ars, 1)))!.Register);
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Import("tld,years,register\n.com,1,10"));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Import("tld,years,register,renew,transfer\n.com,1,10,10,10\n.com,1,11,11,11"));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Import("tld,years,register,renew,transfer\n.com,0,10,10,10"));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Import("tld,years,register,renew,transfer\n.com,1,-5,10,10"));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<TldPricing, IReadOnlyList<ImportedTldPrice>>(p => p.ImportCsv(999999, Currency.Ars, csv, new DateOnly(2026, 11, 2), apply: true)));
    }

    [TestMethod]
    public async Task Busqueda_PrimeroElSistema_DespuesElRegistrar_YParaPuntoArElRdap()
    {
        var rdap = new FakeRdap();
        rdap.Registered["ocupado.com.ar"] = new DateOnly(2027, 9, 3);
        await using var s = await Start(o =>
        {
            o.DomainLookup.Enabled = true;
            o.DomainLookup.BootstrapUrl = FakeRdap.Bootstrap;
            o.DomainLookup.RetryDelayMilliseconds = 1;
            o.DomainLookup.DnsResolvers = ["127.0.0.1:9"];
        }, rdap);
        var h = s.H;
        await Active(s);
        await h.Db(async db =>
        {
            var nicAr = await ManualModule.AddInstance(db, "nic-ar-manual", new JsonObject { ["queue"] = "dominios", ["registry"] = "nic.ar" }, h.Clock.Now);
            db.TldPrices.Add(new TldPrice(".com.ar", nicAr, Currency.Ars, 1, new DateOnly(2026, 1, 1)) { Register = 9000m, Renew = 9000m });
            await db.SaveChangesAsync();
        });

        var results = (await h.Use<DomainSearch, IReadOnlyList<DomainAvailability>>(d => d.Check(["libre.com", "tomado.com", "ejemplo.com", "libre.com.ar", "ocupado.com.ar", "otro.org"], Currency.Ars)))
            .ToDictionary(r => r.Name);
        var candidates = await h.Use<DomainSearch, IReadOnlyList<string>>(d => d.Candidates("MiEmpresa", Currency.Ars));

        Assert.AreEqual((true, "registrar", 15000m), (results["libre.com"].Available, results["libre.com"].Source, results["libre.com"].Offer!.Register!.Value));
        Assert.AreEqual((false, "registrar"), (results["tomado.com"].Available, results["tomado.com"].Source));
        Assert.AreEqual((false, "core"), (results["ejemplo.com"].Available, results["ejemplo.com"].Source));
        Assert.AreEqual((true, "rdap", 9000m), (results["libre.com.ar"].Available, results["libre.com.ar"].Source, results["libre.com.ar"].Offer!.Register!.Value));
        Assert.AreEqual((false, "rdap"), (results["ocupado.com.ar"].Available, results["ocupado.com.ar"].Source));
        Assert.IsNull(results["otro.org"].Offer, "Sin precio no se vende, aunque se sepa si está libre.");
        CollectionAssert.AreEquivalent(new[] { "miempresa.com", "miempresa.com.ar" }, candidates.ToArray());
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<DomainSearch, IReadOnlyList<DomainAvailability>>(d => d.Check(["no es un dominio"], Currency.Ars)));
    }

    [TestMethod]
    public async Task Sincronizacion_UnTrabajoPorDominioYPorDia_TraeVencimientoBloqueoYNameservers()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s);
        s.Fake.Domains["ejemplo.com"].Expires = new DateOnly(2028, 6, 1);
        s.Fake.Domains["ejemplo.com"].Locked = true;

        await h.Tick();
        await h.RunJobs();
        await h.Tick();
        await h.RunJobs();

        var domain = await h.Query(db => db.Domains.SingleAsync(d => d.Id == id));
        Assert.AreEqual((new DateOnly(2028, 6, 1), true, "active"), (domain.ExpiresOn, domain.Locked, domain.RegistryStatus));
        CollectionAssert.AreEqual(new[] { "ns1.viejo.net", "ns2.viejo.net" }, Hosts(domain.Nameservers));
        Assert.IsNotNull(domain.SyncedAt);
        Assert.AreEqual(new DateOnly(2027, 6, 1), domain.PaidThrough, "La sincronización no toca lo pago.");
        Assert.AreEqual(1, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.DomainSync)));
        Assert.AreEqual(1, s.Fake.Calls.Count(c => c.Operation == "get_domain"));
    }

    [TestMethod]
    public async Task Sincronizacion_UnDominioQueSeFueAOtroRegistrar_DejaDeRenovarse()
    {
        await using var s = await Start();
        var h = s.H;
        var id = await Active(s, expires: new DateOnly(2026, 11, 7));
        s.Fake.Domains["ejemplo.com"].Status = "transferred_away";

        await Run<DomainService>(h, d => d.SyncNow(id));
        await h.RunJobs();
        await h.Use<BillingRunService, int>(r => r.Run());

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Cancelled, false, null, "transferred_away"), (domain.Status, domain.AutoRenew, domain.NextDueDate, domain.RegistryStatus));
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
    }

    [TestMethod]
    public async Task Sincronizacion_PuntoArPorRdap_YSiNicNoContestaNoCambiaNada()
    {
        var rdap = new FakeRdap();
        rdap.Registered["ejemplo.com.ar"] = new DateOnly(2027, 9, 3);
        await using var s = await Start(o =>
        {
            o.DomainLookup.Enabled = true;
            o.DomainLookup.BootstrapUrl = FakeRdap.Bootstrap;
            o.DomainLookup.RetryDelayMilliseconds = 1;
            o.DomainLookup.DnsResolvers = ["127.0.0.1:9"];
        }, rdap);
        var h = s.H;
        long synced = 0, failed = 0;
        await h.Db(async db =>
        {
            var nicAr = await ManualModule.AddInstance(db, "nic-ar-manual", new JsonObject { ["queue"] = "dominios", ["registry"] = "nic.ar" }, h.Clock.Now);
            var a = new RegisteredDomain(h.AccountId, "ejemplo.com.ar", nicAr, Currency.Ars, 9000m, h.Clock.Now);
            a.Start(new DateOnly(2026, 9, 3));
            db.Domains.Add(a);
            await db.SaveChangesAsync();
            synced = a.Id;
        });

        await Run<DomainService>(h, d => d.SyncNow(synced));
        await h.RunJobs();
        rdap.Failures = -1;
        await h.Db(async db =>
        {
            var nicAr = (await db.Domains.SingleAsync(d => d.Id == synced)).RegistrarInstanceId;
            var b = new RegisteredDomain(h.AccountId, "otro.com.ar", nicAr, Currency.Ars, 9000m, h.Clock.Now);
            b.Start(new DateOnly(2026, 12, 1));
            db.Domains.Add(b);
            await db.SaveChangesAsync();
            failed = b.Id;
        });
        await Run<DomainService>(h, d => d.SyncNow(failed));
        await h.RunJobs();

        var ok = await h.Query(db => db.Domains.SingleAsync(d => d.Id == synced));
        var down = await h.Query(db => db.Domains.SingleAsync(d => d.Id == failed));
        Assert.AreEqual((new DateOnly(2027, 9, 3), "active"), (ok.ExpiresOn, ok.RegistryStatus));
        Assert.AreEqual((new DateOnly(2026, 12, 1), null, null), (down.ExpiresOn, down.RegistryStatus, down.SyncedAt));
        Assert.AreEqual(0, await h.Query(db => db.StaffTasks.CountAsync()), "Sincronizar nunca abre tareas del staff.");
    }
}
