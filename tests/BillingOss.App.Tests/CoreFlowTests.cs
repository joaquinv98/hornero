using BillingOss.App.Persistence;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Accounts;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BillingOss.App.Tests;

[TestClass]
public sealed class CoreFlowTests
{
    private static Task<PlacedOrder> Order(CoreHarness h, long? planId = null) =>
        h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, [new OrderItem(planId ?? h.PlanId, Label: "vps1.example.com")]));

    private static Task<long> Pay(CoreHarness h, decimal amount, string externalId) =>
        h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", externalId, Currency.Ars, amount, PaymentStatus.Approved)));

    [TestMethod]
    public async Task DosPedidosIdenticosALaVezConLaMismaClave_ElQuePierdeDevuelveElQueQuedo()
    {
        await using var h = await CoreHarness.Start();
        var items = new[] { new OrderItem(h.PlanId) };

        // El primero todavía no confirmó: el segundo no lo ve al buscar la clave y se traba en el índice único al insertar.
        await using var node = h.Node();
        await using var scope = node.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        await using var first = await db.Database.BeginTransactionAsync();
        var inFlight = new Domain.Orders.Order(h.AccountId, Currency.Ars, h.Clock.Now) { ClientReference = "clave-1", RequestFingerprint = OrderService.Fingerprint(h.AccountId, items, null) };
        db.Orders.Add(inFlight);
        await db.SaveChangesAsync();

        var second = h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(h.AccountId, items, clientReference: "clave-1"));
        for (var i = 0; i < 600 && await h.Query(d => d.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync()) == 0; i++)
        {
            await Task.Delay(50);
        }

        await first.CommitAsync();

        // El que perdió el índice único no falla: devuelve el pedido que quedó, y no hay dos.
        Assert.AreEqual(inFlight.Id, (await second).OrderId);
        Assert.AreEqual(1, await h.Query(d => d.Orders.CountAsync()));
    }

    [TestMethod]
    public async Task Pedido_EmiteFacturaConIvaYReservaElPeriodo()
    {
        await using var h = await CoreHarness.Start();

        var placed = await Order(h);

        var invoice = await h.Query(db => db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(1L, invoice.Number);
        Assert.AreEqual(12100m, invoice.Total);
        Assert.AreEqual(10000m, invoice.NetTaxed);
        Assert.AreEqual(2100m, invoice.Tax);
        Assert.AreEqual(InvoiceStatus.Issued, invoice.Status);
        var period = await h.Query(db => db.BilledPeriods.SingleAsync());
        Assert.AreEqual(new DateOnly(2026, 11, 2), period.Start);
        Assert.AreEqual(new DateOnly(2026, 12, 2), period.End);
        var service = await h.Query(db => db.Services.SingleAsync());
        Assert.AreEqual(ServiceStatus.Pending, service.Status);
        Assert.AreEqual(new DateOnly(2026, 12, 2), service.NextDueDate);
    }

    [TestMethod]
    public async Task Pago_PagaLaFactura_ActivaElServicio_YRepetirloNoDuplica()
    {
        await using var h = await CoreHarness.Start();
        var placed = await Order(h);

        var first = await Pay(h, 12100m, "T-1");
        var again = await Pay(h, 12100m, "T-1");
        await h.RunJobs();

        Assert.AreEqual(first, again);
        Assert.AreEqual(1, await h.Query(db => db.Payments.CountAsync()));
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(InvoiceStatus.Paid, invoice.Status);
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Sobrepago_VaASaldo_YLaRenovacionLoConsume()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 20000m, "T-1");
        await h.RunJobs();

        h.Clock.Now = h.Clock.Now.AddDays(25);
        var issued = await h.Use<BillingRunService, int>(s => s.Run());

        Assert.AreEqual(1, issued);
        var renewal = await h.Query(db => db.Documents.SingleAsync(d => d.Reason == "renewal"));
        Assert.AreEqual(12100m, renewal.Total);
        Assert.AreEqual(7900m, renewal.Allocated);
        Assert.AreEqual(4200m, renewal.Pending);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(2, await h.Query(db => db.BilledPeriods.CountAsync()));
    }

    [TestMethod]
    public async Task DosCorridasALaVez_NoDuplicanLaRenovacion()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);

        await Task.WhenAll(
            h.Use<BillingRunService, int>(s => s.Run()),
            h.Use<BillingRunService, int>(s => s.Run()));

        Assert.AreEqual(2, await h.Query(db => db.BilledPeriods.CountAsync()));
        Assert.AreEqual(1, await h.Query(db => db.DocumentLines.CountAsync(l => l.Kind == LineKind.Renewal && l.Description.Contains("02/12/2026"))));
    }

    [TestMethod]
    public async Task NotaDeCreditoTotal_AnulaLiberaElPeriodo_YDevuelveLoPagadoComoSaldo()
    {
        await using var h = await CoreHarness.Start();
        var placed = await Order(h);
        await Pay(h, 12100m, "T-1");

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value,
            [new CreditLine("Anulación", 1, 12100m, TaxTreatment.Taxed, 0.21m)], "Baja por arrepentimiento"));

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(InvoiceStatus.Cancelled, invoice.Status);
        Assert.IsTrue(await h.Query(db => db.BilledPeriods.AllAsync(p => p.Released)));
        Assert.AreEqual(12100m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        var note = await h.Query(db => db.Documents.SingleAsync(d => d.Kind == DocumentKind.CreditNote));
        Assert.AreEqual(1L, note.Number);
        Assert.AreEqual(2100m, note.Tax);
    }

    [TestMethod]
    public async Task Pago_ConIdDeUnaNotaDeCredito_NoSeLeImputa_VaASaldo()
    {
        await using var h = await CoreHarness.Start();
        var placed = await Order(h);
        var noteId = await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value,
            [new CreditLine("Descuento", 1, 1210m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));

        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-1", Currency.Ars, 1000m, PaymentStatus.Approved, [noteId])));

        Assert.AreEqual(0m, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == noteId))).Allocated);
        Assert.AreEqual(1000m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.ActivateServicesOfInvoice)));
    }

    [TestMethod]
    public async Task NotaDeCreditoSobreFacturaPagada_RevierteLasImputaciones_YElSaldoGuardaElPago()
    {
        await using var h = await CoreHarness.Start();
        var placed = await Order(h);
        var paymentId = await Pay(h, 12100m, "T-1");

        await h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value,
            [new CreditLine("Bonificación", 1, 1210m, TaxTreatment.Taxed, 0.21m)], "Ajuste"));

        // Lo imputado de la factura es siempre la suma de sus imputaciones vigentes.
        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        var active = await h.Query(db => db.Allocations.Where(a => a.DocumentId == placed.InvoiceId && a.ReversedAt == null).SumAsync(a => a.Amount));
        Assert.AreEqual(10890m, invoice.Allocated);
        Assert.AreEqual(invoice.Allocated, active);
        Assert.IsTrue(await h.Query(db => db.Allocations.AllAsync(a => a.PaymentId == paymentId)));
        var entry = await h.Query(db => db.CreditEntries.SingleAsync());
        Assert.AreEqual(1210m, entry.Amount);
        Assert.AreEqual(paymentId, entry.PaymentId);
        Assert.AreEqual(12100m, (await h.Query(db => db.Payments.SingleAsync())).Applied);
    }

    [TestMethod]
    public async Task Pedido_ConSaldoAFavor_LoConsume()
    {
        await using var h = await CoreHarness.Start();
        await Pay(h, 5000m, "T-1");

        var placed = await Order(h);

        var invoice = await h.Query(db => db.Documents.SingleAsync(d => d.Id == placed.InvoiceId));
        Assert.AreEqual(5000m, invoice.Allocated);
        Assert.AreEqual(InvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.AreEqual(0m, await h.Query(db => db.CreditBalances.Select(b => b.Balance).SingleAsync()));
    }

    [TestMethod]
    public async Task NotaDeCredito_NoSuperaLoQueFalta()
    {
        await using var h = await CoreHarness.Start();
        var placed = await Order(h);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            h.Use<CreditService, long>(s => s.IssueCreditNote(placed.InvoiceId!.Value,
                [new CreditLine("De más", 1, 13000m, TaxTreatment.Taxed, 0.21m)], "Prueba")));
    }

    [TestMethod]
    public async Task UnDocumentoEmitidoNoSeModifica()
    {
        await using var h = await CoreHarness.Start();
        var placed = await Order(h);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE billing.documents SET total = 1 WHERE id = {placed.InvoiceId}")));
        Assert.AreEqual(PostgresErrorCodes.CheckViolation, ex.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() =>
            h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE billing.document_lines SET amount = 1 WHERE document_id = {placed.InvoiceId}")));
    }

    [TestMethod]
    public async Task Mora_SuspendeYElPagoReactiva()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());

        h.Clock.Now = h.Clock.Now.AddDays(12);
        var suspended = await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();

        Assert.AreEqual(1, suspended);
        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);

        await Pay(h, 12100m, "T-2");
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Mora_ProductoConModuloSinInstancia_NoSeDaPorHecha()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();

        // Como un servicio importado sin vincular: el producto tiene módulo, pero el servicio no tiene instancia.
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => p.Name == "VPS 2 GB")).Kind = ProductKind.Provisioned;
            await db.SaveChangesAsync();
        });
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = h.Clock.Now.AddDays(12);
        Assert.AreEqual(1, await h.Use<DunningService, int>(s => s.Run()));
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == JobKinds.ServiceOperation && j.DedupKey!.Contains(":suspend:")));
        Assert.AreEqual(JobStatus.Dead, job.Status);
        Assert.Contains("sin instancia", job.LastError!);
    }

    [TestMethod]
    public async Task BajaPedidaAFinDePeriodo_NoRenuevaYSeDaDeBajaEseDia()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        var cancelAt = (await h.Query(db => db.Services.SingleAsync())).NextDueDate!.Value;
        await h.Db(async db =>
        {
            (await db.Services.SingleAsync()).CancelAt = cancelAt;
            await db.SaveChangesAsync();
        });

        h.Clock.Now = h.Clock.Now.AddDays(25);
        Assert.AreEqual(0, await h.Use<BillingRunService, int>(s => s.Run()));

        h.Clock.Now = new DateTimeOffset(cancelAt.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync()));
    }

    [TestMethod]
    public async Task BajaPedidaRetenidaEnRevision_EsperaAlStaff_YRechazarlaLaDejaSinEfecto()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();

        // Como una baja inmediata que el sistema anterior no ejecutó: pedida para ayer, con su revisión abierta.
        var service = await h.Query(db => db.Services.SingleAsync());
        await h.Db(async db =>
        {
            (await db.Services.SingleAsync()).CancelAt = DateOnly.FromDateTime(h.Clock.Now.UtcDateTime).AddDays(-1);
            await db.SaveChangesAsync();
        });
        var task = await Review(h, service.Id);

        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.IsFalse(await h.Query(db => db.Jobs.AnyAsync(j => j.Kind == JobKinds.ServiceOperation && j.DedupKey!.Contains(":terminate:"))));

        // Rechazada, la baja pedida queda sin efecto y la mora no la hace.
        await h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Reject(task, "El cliente se quedó con el servicio.");
            return true;
        });
        Assert.IsNull((await h.Query(db => db.Services.SingleAsync())).CancelAt);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task BajaPorMoraRetenidaEnRevision_EsperaAlStaff_YAlCompletarlaSigueLaMora()
    {
        await using var h = await CoreHarness.Start(configure: o => o.TerminationDays = 14);
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = h.Clock.Now.AddDays(25);
        var service = await h.Query(db => db.Services.SingleAsync());
        var task = await Review(h, service.Id);

        // La baja por mora vence, pero queda retenida: la mora no la hace sola.
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);

        // Una baja por mora no se rechaza: se regulariza la deuda.
        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Reject(task, "Que siga.");
            return true;
        }));

        // Al completar la revisión la mora corre en el acto y, como la deuda sigue, lo da de baja.
        await h.Use<StaffTaskService, bool>(async s =>
        {
            await s.Complete(task, new StaffTaskClose());
            return true;
        });
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Terminated, (await h.Query(db => db.Services.SingleAsync())).Status);
        Assert.AreEqual(StaffTaskStatus.Completed, (await h.Query(db => db.StaffTasks.SingleAsync(t => t.Id == task))).Status);
    }

    /// <summary>La tarea de revisión que abre el importador por una baja que el sistema anterior dejó sin ejecutar.</summary>
    internal static Task<long> Review(CoreHarness h, long serviceId) => h.Use<BillingDbContext, long>(async db =>
    {
        var instance = await ManualModule.AddInstance(db, "revisiones", new System.Text.Json.Nodes.JsonObject { ["queue"] = "servicios" }, h.Clock.Now);
        var task = new StaffTask
        {
            ModuleInstanceId = instance,
            Kind = StaffTask.Review,
            Operation = "terminate",
            IdempotencyKey = $"import:service:{serviceId}:terminate",
            Subject = StaffTask.ReviewSubject(serviceId),
            Status = StaffTaskStatus.Open,
            CreatedAt = h.Clock.Now,
        };
        db.StaffTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    });

    [TestMethod]
    public async Task SuspensionQueNoEsPorMora_ElPagoNoLaLevanta()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = h.Clock.Now.AddDays(12);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        await h.Db(async db =>
        {
            (await db.Services.SingleAsync()).SuspendReason = "Pedido del staff";
            await db.SaveChangesAsync();
        });

        await Pay(h, 12100m, "T-2");
        await h.RunJobs();

        Assert.AreEqual(ServiceStatus.Suspended, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Mora_ServicioExentoDeSuspensionAutomatica_NoSeSuspende()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        await h.Db(async db =>
        {
            (await db.Services.SingleAsync()).NoAutoSuspend = true;
            await db.SaveChangesAsync();
        });
        h.Clock.Now = h.Clock.Now.AddDays(25);
        await h.Use<BillingRunService, int>(s => s.Run());
        h.Clock.Now = h.Clock.Now.AddDays(12);

        Assert.AreEqual(0, await h.Use<DunningService, int>(s => s.Run()));
    }

    [TestMethod]
    public async Task PedidoGratis_SinFactura_SeAprovisionaEnseguida()
    {
        await using var h = await CoreHarness.Start();

        var placed = await Order(h, h.FreePlanId);
        await h.RunJobs();

        Assert.IsNull(placed.InvoiceId);
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync()));
        Assert.AreEqual(ServiceStatus.Active, (await h.Query(db => db.Services.SingleAsync())).Status);
    }

    [TestMethod]
    public async Task Pago_ConElMismoIdExternoDesdeOtraCuenta_NoSeConfundeConElPrimero()
    {
        await using var h = await CoreHarness.Start();
        var otherId = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otra cuenta", "otra@example.com", Currency.Ars)));
        await Pay(h, 5000m, "T-9");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => h.Use<PaymentService, long>(s =>
            s.Record(new PaymentInput(otherId, "manual:transferencia", "T-9", Currency.Ars, 5000m, PaymentStatus.Approved))));

        Assert.AreEqual(1, await h.Query(db => db.Payments.CountAsync()));
    }

    [TestMethod]
    public async Task Cuenta_ElCorreoEsUnicoSinDistinguirMayusculas_AlCrearAlCambiarYEnLaBase()
    {
        await using var h = await CoreHarness.Start();
        var repeated = new AccountInput("Otra cuenta", "Cliente@Example.com", Currency.Ars);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => h.Use<AccountService, long>(s => s.Create(repeated)));
        var otherId = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Otra cuenta", "otra@example.com", Currency.Ars)));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => Update(h, otherId, repeated));
        // La propia cuenta puede cambiar las mayúsculas de su correo.
        await Update(h, otherId, new AccountInput("Otra cuenta", "Otra@example.com", Currency.Ars));
        Assert.AreEqual("Otra@example.com", (await h.Query(db => db.Accounts.SingleAsync(a => a.Id == otherId))).Email);

        // Sin pasar por el servicio, la base igual lo rechaza.
        var ex = await Assert.ThrowsExactlyAsync<DbUpdateException>(() => h.Db(db =>
        {
            db.Accounts.Add(new Account("Colada", "CLIENTE@example.com", Currency.Ars, h.Clock.Now));
            return db.SaveChangesAsync();
        }));
        Assert.IsInstanceOfType<PostgresException>(ex.InnerException);
    }

    [TestMethod]
    public async Task Cuenta_DosAltasALaVezConElMismoCorreo_LaSegundaFallaComoValidacion()
    {
        await using var h = await CoreHarness.Start();

        // La primera alta queda abierta sin confirmar: la segunda no la ve en su control previo y espera en el índice único.
        await h.Db(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Accounts.Add(new Account("Primera", "carrera@example.com", Currency.Ars, h.Clock.Now));
            await db.SaveChangesAsync();
            var second = h.Use<AccountService, long>(s => s.Create(new AccountInput("Segunda", "Carrera@example.com", Currency.Ars)));
            await Task.Delay(500);
            Assert.IsFalse(second.IsCompleted, "la segunda alta tendría que esperar a que la primera confirme");
            await tx.CommitAsync();
            await Assert.ThrowsExactlyAsync<ValidationException>(() => second);
        });

        Assert.AreEqual(1, await h.Query(db => db.Accounts.CountAsync(a => a.Email == "carrera@example.com")));
    }

    private static Task<bool> Update(CoreHarness h, long accountId, AccountInput input) => h.Use<AccountService, bool>(async s =>
    {
        await s.Update(accountId, input);
        return true;
    });

    [TestMethod]
    public async Task Planificador_DosTicksNoDuplicanLosTrabajosDelDia_YCorridaYMoraCompartenLaEntidad()
    {
        await using var h = await CoreHarness.Start();

        await Task.WhenAll(h.Tick(), h.Tick());
        await h.Tick();

        var daily = await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.BillingRun || j.Kind == JobKinds.Dunning).ToListAsync());
        Assert.HasCount(2, daily);
        Assert.IsTrue(daily.All(j => j.EntityKey == Scheduler.BillingEntity), "La corrida y la mora corren de a una entre todos los nodos.");
    }

    [TestMethod]
    public async Task CorridaDeFacturacion_ReparteUnTrabajoPorCuenta_YElMismoDiaNoLoRepite()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        var other = await h.Query(async db =>
        {
            var account = new Account("Otro cliente", "otro@example.com", Currency.Ars, h.Clock.Now);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return account.Id;
        });
        await h.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(other, [new OrderItem(h.PlanId)]));
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(other, "manual:transferencia", "T-2", Currency.Ars, 12100m, PaymentStatus.Approved)));
        await h.RunJobs();

        // Una corrida repetida el mismo día ve las mismas cuentas por vencer, pero no las encola dos veces.
        h.Clock.Now = h.Clock.Now.AddDays(25);
        Assert.AreEqual(2, await h.Use<BillingRunService, int>(s => s.Distribute()));
        Assert.AreEqual(2, await h.Use<BillingRunService, int>(s => s.Distribute()));
        var perAccount = await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.BillingAccount).OrderBy(j => j.Id).ToListAsync());
        Assert.HasCount(2, perAccount);
        CollectionAssert.AreEqual(new[] { $"billing.account:2026-11-27:{h.AccountId}", $"billing.account:2026-11-27:{other}" }, perAccount.Select(j => j.DedupKey).ToArray());
        CollectionAssert.AreEqual(new[] { $"account:{h.AccountId}", $"account:{other}" }, perAccount.Select(j => j.EntityKey).ToArray());
        Assert.IsTrue(perAccount.All(j => j.Status == JobStatus.Pending));
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.Reason == "renewal")));

        // El camino de producción: el planificador encola billing.run, que reparte, y los trabajos de cuenta facturan.
        await h.Tick();
        await h.RunJobs();

        Assert.AreEqual(2, await h.Query(db => db.Jobs.CountAsync(j => j.Kind == JobKinds.BillingAccount)));
        Assert.IsTrue(await h.Query(db => db.Jobs.Where(j => j.Kind == JobKinds.BillingAccount || j.Kind == JobKinds.BillingRun).AllAsync(j => j.Status == JobStatus.Done)));
        Assert.AreEqual(2, await h.Query(db => db.Documents.CountAsync(d => d.Reason == "renewal" && d.IssueDate != null)));
    }

    [TestMethod]
    public async Task FacturacionDeUnaCuentaCortadaAntesDeEmitir_ElReintentoEmiteElBorrador()
    {
        await using var h = await CoreHarness.Start();
        await Order(h);
        await Pay(h, 12100m, "T-1");
        await h.RunJobs();
        h.Clock.Now = h.Clock.Now.AddDays(25);

        // La numeración falla después de renovar: el servicio ya avanzó y su borrador quedó sin emitir.
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE billing.counters SET kind = 'Rota' WHERE kind = 'Invoice'"));
        await Assert.ThrowsAsync<Exception>(() => h.Use<BillingRunService, int>(s => s.RunAccount(h.AccountId)));
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE billing.counters SET kind = 'Invoice' WHERE kind = 'Rota'"));
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Reason == "renewal" && d.IssueDate == null)));

        // El reintento del trabajo de la cuenta lo emite enseguida, aunque el servicio ya no esté por vencer.
        Assert.AreEqual(1, await h.Use<BillingRunService, int>(s => s.RunAccount(h.AccountId)));
        Assert.AreEqual(0, await h.Query(db => db.Documents.CountAsync(d => d.IssueDate == null)));
        Assert.AreEqual(1, await h.Query(db => db.Documents.CountAsync(d => d.Reason == "renewal")));
    }
}
