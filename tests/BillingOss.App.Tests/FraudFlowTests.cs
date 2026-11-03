using System.Net;
using System.Text.Json.Nodes;
using BillingOss.App.Identity;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Orders;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BillingOss.App.Tests;

/// <summary>Evaluación de fraude de los pedidos, retención y revisión (diseno/fraude-y-acceso.md, sección 3).</summary>
[TestClass]
public sealed class FraudFlowTests
{
    private static readonly IPAddress Buyer = IPAddress.Parse("203.0.113.7");

    private static Actor Customer { get; } = new(ActorKind.Customer, "user:999", Buyer);

    private static Actor Staff { get; } = new(ActorKind.Staff, "staff:1", IPAddress.Loopback);

    private static Task<PlacedOrder> Order(CoreHarness h, long planId, FraudContext? context = null) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(planId)], fraud: context));

    private static Task<long> Pay(CoreHarness h, string externalId = "T-1") =>
        h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", externalId, Currency.Ars, 12100m, PaymentStatus.Approved)));

    private static Task<FraudDecision> Decide(CoreHarness h, long orderId, bool approve, string? note = null)
    {
        Actor.Current = Staff;
        return h.Use<FraudReviewService, FraudDecision>(s => s.Decide(orderId, approve, note));
    }

    private static Task<FraudCheck> Check(CoreHarness h) => h.Query(db => db.Set<FraudCheck>().SingleAsync());

    private static Task<ServiceStatus> ServiceStatusOf(CoreHarness h) => h.Query(db => db.Services.Select(s => s.Status).SingleAsync());

    private static Task Block(CoreHarness h, BlockKind kind, string value) =>
        h.Use<AccessGuard, long>(g => g.Block(kind, value, "prueba", null, BlockSource.Staff));

    /// <summary>Cuándo se aprovisiona el VPS del catálogo de prueba.</summary>
    private static Task Activation(CoreHarness h, Domain.Catalog.ProductActivation activation) =>
        h.Db(db => db.Products.Where(p => db.Plans.Any(x => x.Id == h.PlanId && x.ProductId == p.Id)).ExecuteUpdateAsync(u => u.SetProperty(p => p.Activation, activation)));

    private static async Task<TestModule> FraudModule(CoreHarness h, FakeFraudModule fake, Platform.SecretBox box, bool reviewOnError = true, params string[] scopes)
    {
        var module = await TestModule.Start(fake.Definition(scopes), h, box);
        await h.Use<SecuritySettingsStore, bool>(async s =>
        {
            await s.Save(new SecuritySettings { FraudEnabled = true, FraudInstanceId = module.InstanceId, FraudReviewOnError = reviewOnError }, 0);
            return true;
        });
        return module;
    }

    [TestMethod]
    public async Task PedidoDelPortal_SinReglasNiModulo_NoSeRetiene()
    {
        await using var h = await CoreHarness.Start();
        Actor.Current = Customer;

        await Order(h, h.FreePlanId);
        await h.RunJobs();

        Assert.AreEqual(0, await h.Query(db => db.Set<FraudCheck>().CountAsync()));
        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task IpEnLaLista_VaARevision_NoSeAprovisionaAunquePague_YAprobarloLoLibera()
    {
        await using var h = await CoreHarness.Start();
        await Block(h, BlockKind.Ip, "203.0.113.0/24");
        Actor.Current = Customer;

        var placed = await Order(h, h.PlanId);
        await Pay(h);
        await h.RunJobs();

        var check = await Check(h);
        Assert.AreEqual(ScreeningStatus.Review, check.Status);
        Assert.AreEqual("blocklist.ip", check.ReasonList.Single().Code);
        Assert.AreEqual(Buyer, check.Ip);
        Assert.AreEqual(ServiceStatus.Pending, await ServiceStatusOf(h), "Un pedido en revisión no se aprovisiona aunque esté pago.");

        var decision = await Decide(h, placed.OrderId, approve: true, "Cliente conocido");
        await h.RunJobs();

        Assert.AreEqual(ScreeningStatus.Approved, decision.Status);
        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));
        check = await Check(h);
        Assert.AreEqual("staff:1", check.DecidedBy);
        Assert.AreEqual("Cliente conocido", check.Note);
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "fraud.approved" && a.Entity == $"order:{placed.OrderId}")));
    }

    [TestMethod]
    public async Task PedidoDelStaffODeLaApiSinIp_NoSeEvalua()
    {
        await using var h = await CoreHarness.Start();
        await Block(h, BlockKind.Ip, "203.0.113.7");
        Actor.Current = Staff;

        await Order(h, h.FreePlanId);
        await h.RunJobs();

        Assert.AreEqual(0, await h.Query(db => db.Set<FraudCheck>().CountAsync()));
        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task CorreoODominioEnLaLista_VaARevision()
    {
        await using var h = await CoreHarness.Start();
        await Block(h, BlockKind.Domain, "@Example.com");
        Actor.Current = Staff;

        await Order(h, h.FreePlanId, new FraudContext(IPAddress.Parse("198.51.100.20")));
        await h.RunJobs();

        Assert.AreEqual("blocklist.domain", (await Check(h)).ReasonList.Single().Code);
        Assert.AreEqual(ServiceStatus.Pending, await ServiceStatusOf(h), "Un pedido de total cero también queda retenido.");
    }

    [TestMethod]
    public async Task Modulo_PuntajeBajo_Pasa_YAprovisionaLoQueSePagoDuranteLaEvaluacion()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var fake = new FakeFraudModule { Score = 12.5m };
        await using var module = await FraudModule(h, fake, box);
        Actor.Current = Staff;

        var placed = await Order(h, h.PlanId, new FraudContext(Buyer, "Mozilla/5.0 prueba"));
        Assert.AreEqual(ScreeningStatus.Pending, (await Check(h)).Status);
        await Pay(h);
        await h.RunJobs();

        var check = await Check(h);
        Assert.AreEqual(ScreeningStatus.Passed, check.Status);
        Assert.AreEqual(12.5m, check.Score);
        Assert.AreEqual("ev-1", check.ExternalId);
        Assert.AreEqual("ip.proxy", check.ReasonList.Single().Code);
        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));

        var (key, input, context) = fake.Calls.Single();
        Assert.AreEqual($"order-{placed.OrderId}-evaluate", key);
        Assert.AreEqual("203.0.113.7", input["request"]!["ip"]!.GetValue<string>());
        Assert.AreEqual("Mozilla/5.0 prueba", input["request"]!["user_agent"]!.GetValue<string>());
        Assert.AreEqual("12100.00", input["order"]!["amount"]!["value"]!.GetValue<string>());
        Assert.AreEqual("VPS 2 GB", input["order"]!["items"]![0]!["name"]!.GetValue<string>());
        Assert.AreEqual("AR", context["account"]!["country"]!.GetValue<string>());
        Assert.IsNull(context["contact"], "Sin el permiso account.contact no viajan nombre ni correo.");
    }

    [TestMethod]
    public async Task Modulo_PuntajeBajo_Pasa_YLuegoElPagoAprovisiona()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var module = await FraudModule(h, new FakeFraudModule { Score = 5m }, box);
        Actor.Current = Staff;

        await Order(h, h.PlanId, new FraudContext(Buyer));
        await h.RunJobs();
        Assert.AreEqual(ScreeningStatus.Passed, (await Check(h)).Status);
        Assert.AreEqual(ServiceStatus.Pending, await ServiceStatusOf(h), "Sin pagar no se aprovisiona.");

        await Pay(h);
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task Modulo_PuntajeAlto_VaARevision_YRechazarAnulaLaFacturaImpaga()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var fake = new FakeFraudModule { Score = 80m };
        await using var module = await FraudModule(h, fake, box, true, "account.contact");
        Actor.Current = Staff;

        var placed = await Order(h, h.PlanId, new FraudContext(Buyer));
        await h.RunJobs();
        Assert.AreEqual(ScreeningStatus.Review, (await Check(h)).Status);
        Assert.AreEqual("cliente@example.com", fake.Calls.Single().Context["contact"]!["email"]!.GetValue<string>());

        var decision = await Decide(h, placed.OrderId, approve: false, "Tarjeta de otro país");

        Assert.AreEqual(ScreeningStatus.Rejected, decision.Status);
        Assert.HasCount(1, decision.CreditNoteIds);
        Assert.AreEqual(OrderStatus.Cancelled, await h.Query(db => db.Orders.Select(o => o.Status).SingleAsync()));
        Assert.AreEqual(ServiceStatus.Cancelled, await ServiceStatusOf(h));
        Assert.AreEqual(InvoiceStatus.Cancelled, await h.Query(db => db.Documents.Where(d => d.Id == placed.InvoiceId).Select(d => d.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task RechazarUnPedidoPago_NoAnulaLaFacturaNiDejaSaldoAFavor()
    {
        await using var h = await CoreHarness.Start();
        await Block(h, BlockKind.Email, "cliente@example.com");
        Actor.Current = Customer;
        var placed = await Order(h, h.PlanId);
        await Pay(h);
        await h.RunJobs();

        var decision = await Decide(h, placed.OrderId, approve: false);

        Assert.AreEqual(12100m, decision.PaidAmount);
        Assert.IsEmpty(decision.CreditNoteIds);
        Assert.AreEqual(InvoiceStatus.Paid, await h.Query(db => db.Documents.Where(d => d.Id == placed.InvoiceId).Select(d => d.Status).SingleAsync()));
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.SumAsync(b => b.Balance)));
        Assert.AreEqual(ServiceStatus.Cancelled, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task CuentaConUnServicioActivo_NoSeMandaAlModulo_ConElAjuste_PeroLasListasValen()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var fake = new FakeFraudModule { Score = 90m };
        await using var module = await TestModule.Start(fake.Definition(), h, box);
        await h.Use<SecuritySettingsStore, bool>(async s =>
        {
            await s.Save(new SecuritySettings { FraudEnabled = true, FraudInstanceId = module.InstanceId, FraudSkipExisting = true }, 0);
            return true;
        });

        // El primero lo evalúa: la cuenta todavía no tiene nada activo.
        Actor.Current = Customer;
        var first = await Order(h, h.FreePlanId);
        await h.RunJobs();
        Assert.HasCount(1, fake.Calls);
        await Decide(h, first.OrderId, approve: true);
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await h.Query(db => db.Services.Select(s => s.Status).SingleAsync()));

        Actor.Current = Customer;
        await Order(h, h.FreePlanId);
        await h.RunJobs();
        Assert.HasCount(1, fake.Calls, "Con un servicio activo no se consulta.");
        Assert.AreEqual(1, await h.Query(db => db.Set<FraudCheck>().CountAsync()));

        await Block(h, BlockKind.Ip, "203.0.113.7");
        Actor.Current = Customer;
        await Order(h, h.FreePlanId);
        await h.RunJobs();
        Assert.AreEqual(1, await h.Query(db => db.Set<FraudCheck>().CountAsync(c => c.Status == ScreeningStatus.Review)), "La lista de bloqueo vale igual.");
        Assert.HasCount(1, fake.Calls);
    }

    [TestMethod]
    public async Task PedidoConDosFacturas_AprobarAprovisionaLoPago_YRechazarAnulaCadaImpaga()
    {
        await using var h = await CoreHarness.Start();
        long net = 0;
        await h.Db(async db =>
        {
            var product = new Domain.Catalog.Product { Name = "Soporte", Kind = Domain.Catalog.ProductKind.Manual };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            var plan = new Domain.Catalog.Plan(product.Id, Domain.Catalog.BillingCycle.Monthly);
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new Domain.Catalog.PlanPrice(plan.Id, Currency.Ars, 10000m, 0m, new DateOnly(2026, 1, 1), PriceMode.TaxExcluded));
            await db.SaveChangesAsync();
            net = plan.Id;
        });
        await Block(h, BlockKind.Ip, "203.0.113.0/24");

        // Un pedido que mezcla modos de carga sale en dos facturas. Pagada solo la primera, aprobarlo aprovisiona lo suyo.
        Actor.Current = Customer;
        var first = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(net)]));
        Assert.HasCount(2, first.InvoiceIds!);
        var firstTotal = await h.Query(db => db.Documents.Where(d => d.Id == first.InvoiceIds![0]).Select(d => d.Total).SingleAsync());
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, firstTotal, PaymentStatus.Approved, [first.InvoiceIds![0]])));
        await Decide(h, first.OrderId, approve: true);
        await h.RunJobs();
        var statuses = await h.Query(db => db.Services.Where(s => first.ServiceIds.Contains(s.Id)).OrderBy(s => s.Id).Select(s => s.Status).ToListAsync());
        CollectionAssert.AreEqual(new[] { ServiceStatus.Active, ServiceStatus.Pending }, statuses, "Lo de la factura impaga espera su pago.");
        var amounts = await h.Use<FraudReviewService, Dictionary<long, (decimal Total, decimal Paid)>>(s => s.Amounts([first.OrderId]));
        Assert.AreEqual(firstTotal, amounts[first.OrderId].Paid);
        Assert.IsTrue(amounts[first.OrderId].Total > firstTotal, "El total suma las dos facturas.");

        // Rechazar un pedido así anula las dos facturas.
        Actor.Current = Customer;
        var second = await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(h.PlanId), new OrderItem(net)]));
        var decision = await Decide(h, second.OrderId, approve: false);
        Assert.HasCount(2, decision.CreditNoteIds);
        CollectionAssert.AreEqual(new[] { InvoiceStatus.Cancelled, InvoiceStatus.Cancelled }, await h.Query(db => db.Documents.Where(d => second.InvoiceIds!.Contains(d.Id)).Select(d => d.Status).ToListAsync()));
    }

    [TestMethod]
    public async Task AprobarUnPedidoDeAltaAlPedir_LoAprovisionaSinEsperarElPago()
    {
        await using var h = await CoreHarness.Start();
        await Activation(h, Domain.Catalog.ProductActivation.OnOrder);
        await Block(h, BlockKind.Ip, "203.0.113.0/24");
        Actor.Current = Customer;
        var placed = await Order(h, h.PlanId);
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Pending, await ServiceStatusOf(h), "Retenido no sale, aunque el producto se active al pedir.");

        await Decide(h, placed.OrderId, approve: true);
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h), "Aprobado sale como al pedir: no espera el pago.");
        Assert.AreEqual(InvoiceStatus.Issued, await h.Query(db => db.Documents.Where(d => d.Id == placed.InvoiceId).Select(d => d.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task AprobarUnPedidoPago_QueAceptaElStaff_NoLoAprovisiona_HastaQueAcepteElPedido()
    {
        await using var h = await CoreHarness.Start();
        await Activation(h, Domain.Catalog.ProductActivation.Manual);
        await Block(h, BlockKind.Ip, "203.0.113.0/24");
        Actor.Current = Customer;
        var placed = await Order(h, h.PlanId);
        await Pay(h);
        await h.RunJobs();

        await Decide(h, placed.OrderId, approve: true);
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Pending, await ServiceStatusOf(h), "Pago y aprobado, espera que el staff acepte el pedido.");
        Assert.AreEqual(OrderStatus.Pending, await h.Query(db => db.Orders.Select(o => o.Status).SingleAsync()));
        await h.Use<OrderService, bool>(async s =>
        {
            await s.Accept(placed.OrderId, provision: true);
            return true;
        });
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task ServicioLeidoAntesDelRechazo_NoSeAprovisionaDespues()
    {
        await using var h = await CoreHarness.Start();
        await Block(h, BlockKind.Ip, "203.0.113.0/24");
        Actor.Current = Customer;
        var placed = await Order(h, h.PlanId);

        // El trabajo que activa lo pago leyó el servicio todavía pendiente, y el rechazo terminó antes de que tomara el lock.
        await h.Use<IServiceProvider, bool>(async sp =>
        {
            var db = sp.GetRequiredService<Persistence.BillingDbContext>();
            var stale = await db.Services.SingleAsync();
            await Decide(h, placed.OrderId, approve: false);
            await using var tx = await db.Database.BeginTransactionAsync();
            await sp.GetRequiredService<BillingCore>().StartProvisioning(stale, CancellationToken.None);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return true;
        });

        Assert.AreEqual(ServiceStatus.Cancelled, await ServiceStatusOf(h));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ServiceOperation)));
    }

    [TestMethod]
    [DataRow(true, ScreeningStatus.Review, ServiceStatus.Pending)]
    [DataRow(false, ScreeningStatus.Passed, ServiceStatus.Active)]
    public async Task ModuloConErrorDefinitivo_SigueElAjusteDeRevisionPorFalla(bool reviewOnError, ScreeningStatus expected, ServiceStatus service)
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        await using var module = await FraudModule(h, new FakeFraudModule { ErrorCode = "invalid_config" }, box, reviewOnError);
        Actor.Current = Staff;

        await Order(h, h.FreePlanId, new FraudContext(Buyer));
        await h.RunJobs();

        var check = await Check(h);
        Assert.AreEqual(expected, check.Status);
        StringAssert.StartsWith(check.Error, "invalid_config");
        Assert.AreEqual("fraud.module_error", check.ReasonList.Single().Code);
        Assert.AreEqual(service, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task ModuloCaido_SeReintentaSinDecidir()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        var fake = new FakeFraudModule { ErrorCode = "upstream_unavailable" };
        await using var module = await FraudModule(h, fake, box);
        Actor.Current = Staff;

        await Order(h, h.FreePlanId, new FraudContext(Buyer));
        await h.RunJobs();

        Assert.AreEqual(ScreeningStatus.Pending, (await Check(h)).Status);
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.FraudScreen));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(1, job.Attempts);
        Assert.AreEqual(ServiceStatus.Pending, await ServiceStatusOf(h));

        // El staff puede decidir sin esperar al módulo, y lo que el módulo devuelva después no cambia esa decisión.
        await Decide(h, (await h.Query(db => db.Orders.SingleAsync())).Id, approve: true);
        fake.ErrorCode = null;
        fake.Score = 99m;
        await h.Db(db => db.Jobs.Where(j => j.Kind == JobKinds.FraudScreen).ExecuteUpdateAsync(u => u.SetProperty(j => j.RunAt, h.Clock.Now)));
        await h.RunJobs();

        Assert.AreEqual(ScreeningStatus.Approved, (await Check(h)).Status);
        Assert.AreEqual(ServiceStatus.Active, await ServiceStatusOf(h));
    }

    [TestMethod]
    public async Task AceptarAMano_UnPedidoRetenido_SeRechaza_YLaDecisionRepetidaNoHaceNada()
    {
        await using var h = await CoreHarness.Start();
        await Block(h, BlockKind.Ip, "203.0.113.7");
        Actor.Current = Customer;
        var placed = await Order(h, h.PlanId);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<OrderService, bool>(async s =>
        {
            await s.Accept(placed.OrderId, provision: true);
            return true;
        }));

        await Decide(h, placed.OrderId, approve: true);
        var again = await Decide(h, placed.OrderId, approve: true);
        Assert.AreEqual(ScreeningStatus.Approved, again.Status);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Decide(h, placed.OrderId, approve: false));
        Assert.AreEqual(1, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "fraud.approved")));
    }

    [TestMethod]
    public void RespuestaDelModulo_SeValidaAntesDeUsarla()
    {
        Assert.IsNull(FraudEvaluation.Parse(JsonNode.Parse("""{"score":"101"}""")));
        Assert.IsNull(FraudEvaluation.Parse(JsonNode.Parse("""{"score":"-1"}""")));
        Assert.IsNull(FraudEvaluation.Parse(JsonNode.Parse("""{"score":"alto"}""")));
        Assert.IsNull(FraudEvaluation.Parse(JsonNode.Parse("""{"reasons":[]}""")));
        Assert.AreEqual(33.34m, FraudEvaluation.Parse(JsonNode.Parse("""{"score":33.335}"""))!.Score);

        var reasons = new JsonArray([.. Enumerable.Range(0, 30).Select(i => (JsonNode)new JsonObject { ["code"] = $"r{i}", ["message"] = new string('x', 400) })]);
        reasons.Insert(0, new JsonObject { ["code"] = "Con Mayúsculas", ["message"] = "se descarta" });
        var parsed = FraudEvaluation.Parse(new JsonObject { ["score"] = "50.00", ["reasons"] = reasons, ["external_id"] = new string('e', 150) })!;

        Assert.HasCount(FraudEvaluation.MaxReasons, parsed.Reasons);
        Assert.AreEqual("r0", parsed.Reasons[0].Code);
        Assert.AreEqual(300, parsed.Reasons[0].Message.Length);
        Assert.AreEqual(100, parsed.ExternalId!.Length);
    }
}
