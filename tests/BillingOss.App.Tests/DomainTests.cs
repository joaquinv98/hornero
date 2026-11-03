using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Domains;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>Dominios: renovación en la corrida, vencimientos y el registrar manual con las reglas de NIC Argentina.</summary>
[TestClass]
public sealed class DomainTests
{
    /// <summary>El núcleo con un .ar de la cuenta de prueba en la instancia manual de NIC Argentina.</summary>
    private static async Task<(CoreHarness H, long Instance, long DomainId)> Start(decimal price, DateOnly expires, string registry = "nic.ar", string name = "ejemplo.com.ar")
    {
        var h = await CoreHarness.Start(TestModule.NewBox());
        long instance = 0;
        long domainId = 0;
        await h.Db(async db =>
        {
            instance = await ManualModule.AddInstance(db, $"{registry}-manual", new JsonObject { ["queue"] = "dominios", ["registry"] = registry, ["grace_days"] = 45 }, h.Clock.Now);
            var domain = new RegisteredDomain(h.AccountId, name, instance, Currency.Ars, price, h.Clock.Now) { DnsManagement = true };
            domain.Start(expires);
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            domainId = domain.Id;
        });
        return (h, instance, domainId);
    }

    private static Task<int> Run(CoreHarness h) => h.Use<BillingRunService, int>(s => s.Run());

    private static JsonObject Context(long domainId, string name = "ejemplo.com.ar") => new()
    {
        ["domain"] = new JsonObject { ["id"] = domainId, ["name"] = name },
        ["account"] = new JsonObject { ["id"] = 1, ["language"] = "es" },
    };

    [TestMethod]
    public async Task RenovacionPaga_SeFacturaUnaVez_YAlPagarseAbreLaTareaDelRegistrar()
    {
        var (h, instance, domainId) = await Start(5000m, new DateOnly(2026, 11, 7));
        await using var _h = h;

        Assert.AreEqual(1, await Run(h));
        Assert.AreEqual(0, await Run(h));

        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync());
        var line = invoice.Lines.Single();
        Assert.AreEqual(5000m, invoice.Total);
        Assert.AreEqual(new DateOnly(2026, 11, 7), invoice.DueDate);
        Assert.AreEqual(domainId, line.DomainId);
        Assert.AreEqual("Renovación del dominio ejemplo.com.ar, 1 año (07/11/2026 al 06/11/2027) + gestión de DNS", line.Description);

        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 5000m, PaymentStatus.Approved)));
        await h.RunJobs();

        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual(new DateOnly(2027, 11, 7), domain.PaidThrough);
        Assert.AreEqual(new DateOnly(2027, 11, 7), domain.NextDueDate);
        var op = await h.Query(db => db.DomainOperations.SingleAsync());
        Assert.AreEqual("renew", op.Operation);
        StringAssert.Contains(op.Input, "2026-11-07");
        var task = await h.Query(db => db.StaffTasks.SingleAsync());
        Assert.AreEqual(("registrar", "renew", StaffTaskStatus.Open), (task.Kind, task.Operation, task.Status));

        await h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Complete(task.Id, new StaffTaskClose(ExpiresOn: new DateOnly(2027, 11, 7)));
            return true;
        });
        await h.RunJobs();

        Assert.AreEqual(DomainOperationStatus.Completed, (await h.Query(db => db.DomainOperations.SingleAsync())).Status);
        Assert.AreEqual(new DateOnly(2027, 11, 7), (await h.Query(db => db.Domains.SingleAsync())).ExpiresOn);
        var known = await ManualModuleTests.Call(h, instance, "/v0/registrar/get_domain", Context(domainId));
        Assert.AreEqual("2027-11-07", known.Response!.Result!["expires_on"]!.GetValue<string>());
        Assert.AreEqual("staff", known.Response.Result["source"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Renovacion_ConElModoDeCargaCongeladoEnElDominio_VaEnUnBorradorPorModo_ComoServicioDigital()
    {
        var (h, instance, _) = await Start(5000m, new DateOnly(2026, 11, 7));
        await using var _h = h;
        await h.Db(async db =>
        {
            (await db.Domains.SingleAsync()).PriceMode = PriceMode.TaxExcluded;
            var other = new RegisteredDomain(h.AccountId, "otro.com.ar", instance, Currency.Ars, 5000m, h.Clock.Now);
            other.Start(new DateOnly(2026, 11, 7));
            db.Domains.Add(other);
            await db.SaveChangesAsync();
        });

        Assert.AreEqual(2, await Run(h));

        var invoices = await h.Query(db => db.Documents.Include(d => d.Lines).ToListAsync());
        var excluded = invoices.Single(d => d.PriceMode == PriceMode.TaxExcluded);
        Assert.AreEqual(6050m, excluded.Total, "Sin impuesto: el IVA se suma al precio congelado.");
        Assert.AreEqual((TaxCategories.Digital, 0.21m), (excluded.Lines.Single().Category, excluded.Lines.Single().Rate));
        Assert.AreEqual(5000m, invoices.Single(d => d.PriceMode == PriceMode.TaxIncluded).Total);
    }

    [TestMethod]
    public async Task PrecioCero_AvanzaElPeriodoYPideLaRenovacionSinFactura()
    {
        var (h, _, _) = await Start(0m, new DateOnly(2026, 11, 7));
        await using var _h = h;

        await Run(h);
        await Run(h);
        await h.RunJobs();

        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual(new DateOnly(2027, 11, 7), domain.NextDueDate);
        Assert.AreEqual(new DateOnly(2027, 11, 7), domain.PaidThrough);
        Assert.AreEqual("renew", (await h.Query(db => db.DomainOperations.SingleAsync())).Operation);
        Assert.AreEqual(StaffTaskStatus.Open, (await h.Query(db => db.StaffTasks.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task RenovacionImpaga_PasaAGracia_YALos45DiasQuedaVencido()
    {
        var (h, _, _) = await Start(5000m, new DateOnly(2026, 11, 5));
        await using var _h = h;
        await Run(h);

        h.Clock.Now = h.Clock.Now.AddDays(4);
        await Run(h);
        Assert.AreEqual(DomainStatus.Grace, (await h.Query(db => db.Domains.SingleAsync())).Status);

        h.Clock.Now = new DateTimeOffset(2026, 12, 20, 15, 0, 0, TimeSpan.Zero);
        await Run(h);

        Assert.AreEqual(DomainStatus.Expired, (await h.Query(db => db.Domains.SingleAsync())).Status);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()));
    }

    [TestMethod]
    public async Task VencidoQuePagaTarde_ElManualRechazaLaRenovacion_YLaNotaDeCreditoRetrocedeLoPago()
    {
        var (h, _, domainId) = await Start(5000m, new DateOnly(2026, 11, 7));
        await using var _h = h;
        await Run(h);
        h.Clock.Now = new DateTimeOffset(2026, 12, 23, 15, 0, 0, TimeSpan.Zero);
        await Run(h);
        var invoiceId = await h.Query(db => db.Documents.Select(d => d.Id).SingleAsync());

        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 5000m, PaymentStatus.Approved)));
        await h.RunJobs();

        // Pasada la gracia, NIC Argentina ya liberó el nombre: el manual rechaza sin abrir tarea y el dominio sigue vencido, pago un año más.
        var op = await h.Query(db => db.DomainOperations.SingleAsync());
        Assert.AreEqual((DomainOperationStatus.Failed, "renew"), (op.Status, op.Operation));
        StringAssert.Contains(op.LastError, "rejected");
        var domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Expired, new DateOnly(2027, 11, 7)), (domain.Status, domain.PaidThrough));
        Assert.AreEqual(0, await h.Query(db => db.StaffTasks.CountAsync()));

        // El staff anula la renovación con la nota de crédito: lo pagado vuelve como saldo a favor y lo pago retrocede.
        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == invoiceId));
        var lines = invoice.Lines.Select(l => new CreditLine(l.Description, l.Quantity, l.UnitPrice, l.Treatment, l.Rate, l.ServiceId)).ToList();
        await h.Use<CreditService, long>(c => c.IssueCreditNote(invoiceId, lines, "El registro liberó el dominio"));

        domain = await h.Query(db => db.Domains.SingleAsync());
        Assert.AreEqual((DomainStatus.Expired, new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 7)), (domain.Status, domain.PaidThrough, domain.NextDueDate));
        Assert.AreEqual(5000m, (await h.Query(db => db.CreditBalances.SingleAsync(b => b.AccountId == h.AccountId))).Balance);
        Assert.AreEqual(0, await Run(h), "Vencido, la corrida no lo vuelve a facturar.");
    }

    [TestMethod]
    public async Task VencidoHaceMasQueLaGracia_NoSeFactura()
    {
        var (h, _, _) = await Start(5000m, new DateOnly(2025, 3, 1));
        await using var _h = h;

        await Run(h);

        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
        Assert.AreEqual(DomainStatus.Expired, (await h.Query(db => db.Domains.SingleAsync())).Status);
    }

    /// <summary>Reglas de .ar en el registrar manual. Las fechas son contra el 2026-11-02 del reloj de prueba.</summary>
    [TestMethod]
    [DataRow("renew", """{"years":2,"current_expiry":"2026-11-12"}""", "invalid_input")]
    [DataRow("renew", """{"years":1,"current_expiry":"2026-12-12"}""", "conflict")]
    [DataRow("renew", """{"years":1,"current_expiry":"2026-12-02"}""", "pending")]
    [DataRow("renew", """{"years":1,"current_expiry":"2026-09-18"}""", "pending")]
    [DataRow("renew", """{"years":1,"current_expiry":"2026-09-17"}""", "rejected")]
    [DataRow("renew", """{"years":1}""", "invalid_input")]
    [DataRow("register", """{"name":"nuevo.com.ar","years":2,"nameservers":["ns1.example.net","ns2.example.net"]}""", "invalid_input")]
    [DataRow("register", """{"name":"nuevo.com.ar","years":1,"nameservers":["ns1.example.net","ns2.example.net"]}""", "pending")]
    [DataRow("set_nameservers", """{"nameservers":["ns1.example.net"]}""", "invalid_input")]
    [DataRow("set_nameservers", """{"nameservers":["ns1.example.net","ns2.example.net"]}""", "pending")]
    [DataRow("check", """{"names":["libre.com.ar"]}""", "completed")]
    [DataRow("get_domain", "{}", "completed")]
    [DataRow("transfer", "{}", "not_supported")]
    [DataRow("get_auth_code", "{}", "not_supported")]
    [DataRow("set_lock", "{}", "not_supported")]
    [DataRow("set_contacts", "{}", "not_supported")]
    [DataRow("get_prices", "{}", "not_supported")]
    public async Task RegistrarManual_ReglasDeNicArgentina(string operation, string input, string expected)
    {
        var (h, instance, domainId) = await Start(5000m, new DateOnly(2027, 6, 1));
        await using var _h = h;

        var result = await ManualModuleTests.Call(h, instance, $"/v0/registrar/{operation}", Context(domainId), JsonNode.Parse(input)!.AsObject(), $"dom-{domainId}-{operation}-1");

        var actual = result.Error?.Code ?? result.Response!.Status;
        Assert.AreEqual(expected, actual);
        if (expected == "conflict")
        {
            // Antes de los 30 días previos al vencimiento la tarea espera hasta que abra la ventana.
            Assert.AreEqual(10 * 86400, result.Error!.RetryAfter);
        }

        if (operation == "check")
        {
            var answer = result.Response!.Result!["results"]![0]!;
            Assert.IsNull(answer["available"]);
            Assert.AreEqual("requires_staff", answer["reason"]!.GetValue<string>());
        }

        if (operation == "get_domain")
        {
            Assert.AreEqual("unknown", result.Response!.Result!["status"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task RegistrarManual_ZonaPropia_DisponibleSiNoExisteEnElNucleo_YSinRed()
    {
        var (h, instance, domainId) = await Start(0m, new DateOnly(2027, 6, 1), "own_dns", "cliente.proveedor.ar");
        await using var _h = h;

        var result = await ManualModuleTests.Call(h, instance, "/v0/registrar/check", Context(domainId), new JsonObject { ["names"] = new JsonArray("cliente.proveedor.ar", "nuevo.proveedor.ar") });

        var results = result.Response!.Result!["results"]!.AsArray();
        Assert.IsFalse(results[0]!["available"]!.GetValue<bool>());
        Assert.IsTrue(results[1]!["available"]!.GetValue<bool>());

        // El módulo manual no recibe ningún cliente de red: solo la base, los ajustes y el reloj.
        var dependencies = typeof(ManualModule).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.IsFalse(dependencies.Any(t => t == typeof(HttpClient) || t == typeof(ModuleClient)));
    }
}
