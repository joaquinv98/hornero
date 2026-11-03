using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BillingOss.App.Notifications;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Billing;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Payments;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Domain.Usage;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Módulo falso de aprovisionamiento con uso medido, hecho con la SDK: crea, suspende y reactiva enseguida, y lee las
/// métricas que la prueba deja en <see cref="Readings"/>. Con <see cref="FailWith"/> responde ese error.
/// </summary>
internal sealed class FakeMeter
{
    private int calls;

    public ConcurrentDictionary<string, decimal> Readings { get; } = new();

    public ConcurrentQueue<string> Operations { get; } = new();

    public string? FailWith { get; set; }

    public int Calls => Volatile.Read(ref calls);

    public static JsonObject Manifest(bool usage = true) => new()
    {
        ["id"] = "medidor",
        ["capabilities"] = usage ? new JsonArray("provisioning.core", UsageProtocol.Capability) : new JsonArray("provisioning.core"),
        ["usage_metrics"] = new JsonArray(
            new JsonObject { ["id"] = "transfer_gb", ["kind"] = UsageProtocol.Counter, ["unit"] = "GB", ["label"] = new JsonObject { ["es"] = "Transferencia", ["en"] = "Transfer" } },
            new JsonObject { ["id"] = "disk_gb", ["kind"] = UsageProtocol.Gauge, ["unit"] = "GB", ["label"] = new JsonObject { ["es"] = "Disco", ["en"] = "Disk" } },
            new JsonObject { ["id"] = "mailboxes", ["kind"] = UsageProtocol.Gauge, ["unit"] = "casillas", ["label"] = new JsonObject { ["es"] = "Casillas", ["en"] = "Mailboxes" } }),
    };

    public void Set(decimal transfer, decimal disk)
    {
        Readings["transfer_gb"] = transfer;
        Readings["disk_gb"] = disk;
    }

    public ModuleDefinition Definition() => Lifecycle(new ModuleDefinition(Manifest()))
        .Usage((_, metrics) =>
        {
            Interlocked.Increment(ref calls);
            if (FailWith is { } code)
            {
                throw new ModuleException(code, "El hipervisor no responde", retryAfter: 60);
            }

            return Task.FromResult<IReadOnlyDictionary<string, decimal>>(metrics.Where(Readings.ContainsKey).ToDictionary(m => m, m => Readings[m]));
        });

    /// <summary>Un módulo que lee mal: devuelve el texto tal cual, sin pasar por la SDK.</summary>
    public ModuleDefinition Raw(string value) => Lifecycle(new ModuleDefinition(Manifest()))
        .Operation("provisioning", UsageProtocol.Operation, _ =>
            Task.FromResult(ModuleResponse.Completed(new JsonObject { ["metrics"] = new JsonObject { ["disk_gb"] = value } })), mutating: false);

    public ModuleDefinition Lifecycle(ModuleDefinition definition) => definition
        .Operation("provisioning", "create", _ => Done("create", "active"))
        .Operation("provisioning", "suspend", _ => Done("suspend", "suspended"))
        .Operation("provisioning", "unsuspend", _ => Done("unsuspend", "active"))
        .Operation("provisioning", "terminate", _ => Done("terminate", "terminated"));

    private Task<ModuleResponse> Done(string operation, string observed)
    {
        Operations.Enqueue(operation);
        return Task.FromResult(ModuleResponse.Completed(new JsonObject { ["observed_state"] = observed }));
    }
}

/// <summary>El núcleo con el módulo medidor ligado al producto "VPS 2 GB".</summary>
internal sealed class UsageWorld(CoreHarness h, TestModule module, long productId) : IAsyncDisposable
{
    public CoreHarness H { get; } = h;

    public TestModule Module { get; } = module;

    public long ProductId { get; } = productId;

    public static async Task<UsageWorld> Start(ModuleDefinition definition, Action<BillingOptions>? configure = null)
    {
        var box = TestModule.NewBox();
        var h = await CoreHarness.Start(box, configure);
        var module = await TestModule.Start(definition, h, box);
        var productId = await h.Query(db => db.Products.Where(p => p.Name == "VPS 2 GB").Select(p => p.Id).SingleAsync());
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => p.Id == productId)).ModuleInstanceId = module.InstanceId;
            await db.SaveChangesAsync();
        });
        return new UsageWorld(h, module, productId);
    }

    public Task<UsageMetric> Metric(string code, decimal included, UsageCycle cycle = UsageCycle.Renewal, UsageAggregation? aggregation = null, decimal step = 1m, int[]? alerts = null, bool active = true) =>
        H.Use<UsageCatalog, UsageMetric>(c => c.Save(ProductId, code, new UsageMetricInput(null, null, aggregation, included, step, cycle, alerts, active)));

    public Task<UsageMetricPrice> Price(string code, decimal unitPrice, Currency? currency = null, DateOnly? from = null) =>
        H.Use<UsageCatalog, UsageMetricPrice>(c => c.AddPrice(ProductId, code, currency ?? Currency.Ars, unitPrice, from ?? new DateOnly(2026, 1, 1)));

    /// <summary>Un servicio pago, activo desde el 02/11/2026 y pago hasta el 02/12/2026 (o el 02/11/2027 con el plan anual).</summary>
    public async Task<long> ActiveService(long? accountId = null, long? planId = null)
    {
        var account = accountId ?? H.AccountId;
        var placed = await H.Use<OrderService, PlacedOrder>(s => s.PlaceOrder(account, [new OrderItem(planId ?? H.PlanId, Label: "vps-01")]));
        if (placed.InvoiceId is { } invoice)
        {
            var doc = await H.Query(db => db.Documents.SingleAsync(d => d.Id == invoice));
            await H.Use<PaymentService, long>(s => s.Record(new PaymentInput(account, "manual:transferencia", $"T-{placed.OrderId}", doc.Currency, doc.Total, PaymentStatus.Approved)));
        }

        await H.RunJobs();
        Assert.AreEqual(ServiceStatus.Active, await H.Query(db => db.Services.Where(s => s.Id == placed.ServiceIds[0]).Select(s => s.Status).SingleAsync()));
        return placed.ServiceIds[0];
    }

    /// <summary>La lectura de un día: el planificador la pide al empezar el día siguiente, 00:30 en Buenos Aires.</summary>
    public async Task Day(DateOnly date)
    {
        H.Clock.Now = new DateTimeOffset(date.AddDays(1), new TimeOnly(3, 30), TimeSpan.Zero);
        await H.Tick();
        await H.RunJobs();
    }

    /// <summary>Muestras ya guardadas, una por día desde <paramref name="from"/>: para probar los cortes sin leer día por día.</summary>
    public Task Samples(long serviceId, string code, DateOnly from, params decimal[] values) => H.Db(async db =>
    {
        for (var i = 0; i < values.Length; i++)
        {
            db.Add(new UsageSample(serviceId, code, from.AddDays(i), values[i], values[i], H.Clock.Now));
        }

        await db.SaveChangesAsync();
    });

    public Task<List<Document>> Invoices(string reason) =>
        H.Query(db => db.Documents.Include(d => d.Lines).Where(d => d.Kind == DocumentKind.Invoice && d.Reason == reason && d.IssueDate != null).OrderBy(d => d.Id).ToListAsync());

    public async ValueTask DisposeAsync()
    {
        await Module.DisposeAsync();
        await H.DisposeAsync();
    }
}

/// <summary>Facturación por uso medido de punta a punta (diseno/uso-medido.md), con el módulo falso por el protocolo.</summary>
[TestClass]
public sealed class UsageFlowTests
{
    private static readonly DateOnly Nov2 = new(2026, 11, 2);

    [TestMethod]
    public async Task LecturaDiaria_MedidorYContador_UnaMuestraPorDia_YAvisaAlPasarLosUmbrales()
    {
        var meter = new FakeMeter();
        await using var w = await UsageWorld.Start(meter.Definition());
        var h = w.H;

        // Sin métricas, el planificador no encola nada del uso.
        await h.Tick();
        Assert.AreEqual(0, await h.Query(db => db.Jobs.CountAsync(j => j.Kind.StartsWith("usage."))));

        await w.Metric("transfer_gb", included: 100m);
        await w.Metric("disk_gb", included: 10m, UsageCycle.CalendarMonth);

        // El precio sube a mitad de la ventana: el corte cobra el del día en que empezó, y el aviso dice ese.
        await w.Price("transfer_gb", 500m);
        await w.Price("transfer_gb", 900m, from: Nov2.AddDays(1));
        var id = await w.ActiveService();

        meter.Set(transfer: 5000m, disk: 4m);
        await w.Day(Nov2);
        meter.Set(transfer: 5070m, disk: 9m);
        await w.Day(Nov2.AddDays(1));
        meter.Set(transfer: 5090m, disk: 9.5m);
        await w.Day(Nov2.AddDays(2));

        // La VM se reinició: el contador volvió a cero y lo que marca es lo usado desde entonces.
        meter.Set(transfer: 15m, disk: 12m);
        await w.Day(Nov2.AddDays(3));

        var samples = await h.Query(db => db.Set<UsageSample>().Where(s => s.ServiceId == id).OrderBy(s => s.Code).ThenBy(s => s.Date).ToListAsync());
        CollectionAssert.AreEqual(new[] { 4m, 9m, 9.5m, 12m }, samples.Where(s => s.Code == "disk_gb").Select(s => s.Value).ToArray());
        CollectionAssert.AreEqual(new[] { 0m, 70m, 20m, 15m }, samples.Where(s => s.Code == "transfer_gb").Select(s => s.Value).ToArray(), "La primera lectura es la base.");
        CollectionAssert.AreEqual(new[] { 5000m, 5070m, 5090m, 15m }, samples.Where(s => s.Code == "transfer_gb").Select(s => s.Reading).ToArray());
        CollectionAssert.AreEqual(new[] { Nov2, Nov2.AddDays(1), Nov2.AddDays(2), Nov2.AddDays(3) }, samples.Where(s => s.Code == "disk_gb").Select(s => s.Date).ToArray());
        Assert.AreEqual(4, meter.Calls);

        // Repetir la lectura de un día no llama al módulo ni guarda otra muestra.
        await h.Use<IEnumerable<IJobHandler>, JobResult>(handlers => handlers.Single(x => x.Kind == UsageJobs.Read)
            .Handle(new ClaimedJob(0, UsageJobs.Read, $$"""{"service_id":{{id}},"date":"2026-11-05"}""", 1, 10), CancellationToken.None));
        Assert.AreEqual(4, meter.Calls);
        Assert.AreEqual(8, await h.Query(db => db.Set<UsageSample>().CountAsync()));

        // Un aviso por umbral, el mayor alcanzado, sin repetir: 80 y 100 % del disco y de la transferencia.
        var emails = (await h.Query(db => db.Set<OutboundEmail>().Where(e => e.Template == TemplateKeys.UsageThreshold).ToListAsync()))
            .ToDictionary(e => e.DedupKey);
        CollectionAssert.AreEquivalent(
            new[] { $"usage-threshold:{id}:disk_gb:0:80", $"usage-threshold:{id}:transfer_gb:0:80", $"usage-threshold:{id}:disk_gb:0:100", $"usage-threshold:{id}:transfer_gb:0:100" },
            emails.Keys.ToArray());
        var disk = emails[$"usage-threshold:{id}:disk_gb:0:80"];
        Assert.AreEqual("Usaste el 80 % de Disco en VPS 2 GB (vps-01)", disk.Subject);
        StringAssert.Contains(disk.Html, "9 GB");
        var over = emails[$"usage-threshold:{id}:transfer_gb:0:100"];
        Assert.AreEqual("Llegaste a lo incluido de Transferencia en VPS 2 GB (vps-01)", over.Subject);
        StringAssert.Contains(over.Html, "105 GB");
        StringAssert.Contains(over.Html, "ARS 500,00");
        Assert.IsFalse(over.Html.Contains("ARS 900", StringComparison.Ordinal), over.Html);
        StringAssert.Contains(over.Html, "/servicios/" + id + "/uso");

        // Inactiva no se lee. Al volver, la primera lectura es una base nueva: lo que subió en el medio no se cobra.
        await w.Metric("transfer_gb", included: 100m, active: false);
        meter.Set(transfer: 9000m, disk: 12m);
        await w.Day(Nov2.AddDays(4));
        await w.Metric("transfer_gb", included: 100m);
        meter.Set(transfer: 9010m, disk: 12m);
        await w.Day(Nov2.AddDays(5));
        meter.Set(transfer: 9030m, disk: 12m);
        await w.Day(Nov2.AddDays(6));
        var resumed = await h.Query(db => db.Set<UsageSample>().Where(s => s.ServiceId == id && s.Code == "transfer_gb" && s.Date > Nov2.AddDays(3))
            .OrderBy(s => s.Date).Select(s => new { s.Date, s.Value }).ToListAsync());
        Assert.AreEqual(2, resumed.Count, "El día inactivo no tiene muestra.");
        Assert.AreEqual((Nov2.AddDays(5), 0m), (resumed[0].Date, resumed[0].Value));
        Assert.AreEqual((Nov2.AddDays(6), 20m), (resumed[1].Date, resumed[1].Value));
    }

    [TestMethod]
    public async Task LecturaDiaria_ModuloCaidoReintenta_LecturaInvalidaOSinCapacidadQuedaMuerta()
    {
        var meter = new FakeMeter { FailWith = ErrorCodes.UpstreamUnavailable };
        await using var w = await UsageWorld.Start(meter.Definition());
        var h = w.H;
        await w.Metric("disk_gb", included: 10m);
        var id = await w.ActiveService();
        meter.Readings["disk_gb"] = 3m;

        // Caído: el trabajo vuelve a la cola con la espera que pidió el módulo, y anda cuando el módulo vuelve.
        await w.Day(Nov2);
        var job = await h.Query(db => db.Jobs.SingleAsync(j => j.Kind == UsageJobs.Read));
        Assert.AreEqual(JobStatus.Pending, job.Status);
        StringAssert.Contains(job.LastError, ErrorCodes.UpstreamUnavailable);
        meter.FailWith = null;
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.RunJobs();
        Assert.AreEqual(3m, await h.Query(db => db.Set<UsageSample>().Where(s => s.ServiceId == id).Select(s => s.Value).SingleAsync()));

        // Una lectura con signo no se guarda: el trabajo queda muerto y visible para el staff.
        await using var raw = await UsageWorld.Start(new FakeMeter().Raw("-3"));
        await raw.Metric("disk_gb", included: 10m);
        await raw.ActiveService();
        await raw.Day(Nov2);
        var dead = await raw.H.Query(db => db.Jobs.SingleAsync(j => j.Kind == UsageJobs.Read));
        Assert.AreEqual(JobStatus.Dead, dead.Status);
        StringAssert.Contains(dead.LastError, "lectura inválida de disk_gb");
        Assert.AreEqual(0, await raw.H.Query(db => db.Set<UsageSample>().CountAsync()));

        // Un módulo que dejó de declarar la capacidad tampoco se lee.
        await raw.H.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE platform.module_installations SET manifest = manifest - 'capabilities'"));
        await raw.Day(Nov2.AddDays(1));
        var undeclared = await raw.H.Query(db => db.Jobs.Where(j => j.Kind == UsageJobs.Read).OrderByDescending(j => j.Id).FirstAsync());
        Assert.AreEqual(JobStatus.Dead, undeclared.Status);
        StringAssert.Contains(undeclared.LastError, UsageProtocol.Capability);
    }

    [TestMethod]
    public async Task ConLaRenovacion_ElExcedenteVaEnUnRenglonAparte_SinDuplicar_YLaVentanaSigue()
    {
        await using var w = await UsageWorld.Start(new FakeMeter().Definition());
        var h = w.H;
        await w.Metric("transfer_gb", included: 100m);
        await w.Price("transfer_gb", 500m);
        var id = await w.ActiveService();
        await w.Samples(id, "transfer_gb", Nov2, [.. Enumerable.Repeat(5m, 23)]);

        h.Clock.Now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        await h.Use<BillingRunService, int>(s => s.Run());

        // El corte es ayer, 24/11: entran las muestras del 2 al 23 (110 GB) y la del 24 queda para la ventana siguiente.
        var invoice = (await w.Invoices("renewal")).Single();
        var usage = invoice.Lines.Single(l => l.Kind == LineKind.Usage);
        Assert.AreEqual(1, invoice.Lines.Count(l => l.Kind == LineKind.Renewal));
        Assert.AreEqual("Uso de Transferencia de VPS 2 GB (vps-01) del 02/11/2026 al 23/11/2026: 110 GB medidos, 100 GB incluidos", usage.Description);
        Assert.AreEqual(10m, usage.Quantity);
        Assert.AreEqual(500m, usage.UnitPrice);
        Assert.AreEqual(5000m, usage.Amount);
        Assert.AreEqual(id, usage.ServiceId);
        Assert.AreEqual(0.21m, usage.Rate);
        Assert.AreEqual(17100m, invoice.Total);

        var charge = await h.Query(db => db.Set<UsageCharge>().SingleAsync());
        Assert.AreEqual((Nov2, new DateOnly(2026, 11, 24), 110m, 10m, usage.Id), (charge.Start, charge.End, charge.Measured, charge.Excess, charge.DocumentLineId));
        Assert.AreEqual(22, await h.Query(db => db.Set<UsageSample>().CountAsync(s => s.ChargeId == charge.Id)));

        var view = (await h.Use<UsageQueries, UsageView?>(q => q.ForService(id)))!;
        var open = view.Metrics.Single();
        Assert.AreEqual((5m, (DateOnly?)new DateOnly(2026, 11, 24), 0m), (open.Used, open.Since, open.Excess));
        Assert.AreEqual(5000m, view.Charges.Single().Amount);
        Assert.AreEqual(invoice.Number, view.Charges.Single().DocumentNumber);

        // Pagar la renovación con su excedente avanza lo pagado una sola vez.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-2", Currency.Ars, 17100m, PaymentStatus.Approved)));
        Assert.AreEqual(new DateOnly(2027, 1, 2), await h.Query(db => db.Services.Where(s => s.Id == id).Select(s => s.PaidThrough).SingleAsync()));

        // Una muestra que llega tarde, con fecha de la ventana ya cortada, entra en la siguiente, que sigue desde el corte.
        await w.Samples(id, "transfer_gb", new DateOnly(2026, 11, 1), 5m);
        var late = (await h.Use<UsageQueries, UsageView?>(q => q.ForService(id)))!.Metrics.Single();
        Assert.AreEqual((10m, (DateOnly?)new DateOnly(2026, 11, 24)), (late.Used, late.Since));
        h.Clock.Now = new DateTimeOffset(2026, 12, 26, 12, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        var next = await h.Query(db => db.Set<UsageCharge>().OrderByDescending(c => c.Id).FirstAsync());
        Assert.AreEqual((new DateOnly(2026, 11, 24), new DateOnly(2026, 12, 25), 10m, (long?)null), (next.Start, next.End, next.Measured, next.DocumentLineId));
    }

    [TestMethod]
    public async Task PlanAnual_LoQueSeSumaIncluyeDoceMeses_YUnNivelLoMismo()
    {
        await using var w = await UsageWorld.Start(new FakeMeter().Definition());
        var h = w.H;
        var annual = await h.Query(async db =>
        {
            var plan = new Plan(w.ProductId, new BillingCycle(CycleUnit.Year));
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanPrices.Add(new PlanPrice(plan.Id, Currency.Ars, 120000m, 0m, new DateOnly(2026, 1, 1)));
            await db.SaveChangesAsync();
            return plan.Id;
        });
        await w.Metric("transfer_gb", included: 100m);
        await w.Metric("disk_gb", included: 10m);
        await w.Price("transfer_gb", 500m);
        await w.Price("disk_gb", 1000m);
        var id = await w.ActiveService(planId: annual);
        await w.Samples(id, "transfer_gb", Nov2, [.. Enumerable.Repeat(50m, 26)]);
        await w.Samples(id, "disk_gb", Nov2, [.. Enumerable.Repeat(12m, 26)]);

        // La transferencia incluye 100 GB por mes, 1200 en el año. El disco incluye 10 GB en cualquier plan.
        var view = (await h.Use<UsageQueries, UsageView?>(q => q.ForService(id)))!;
        Assert.AreEqual((1200m, 100m), (view.Metrics.Single(m => m.Code == "transfer_gb").Included, view.Metrics.Single(m => m.Code == "transfer_gb").Excess));
        Assert.AreEqual((10m, 2m), (view.Metrics.Single(m => m.Code == "disk_gb").Included, view.Metrics.Single(m => m.Code == "disk_gb").Excess));

        h.Clock.Now = new DateTimeOffset(2027, 10, 27, 12, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());
        var lines = (await w.Invoices("renewal")).Single(d => d.Lines.Any(l => l.Kind == LineKind.Usage)).Lines.Where(l => l.Kind == LineKind.Usage).OrderBy(l => l.UnitPrice).ToList();
        Assert.AreEqual((100m, 500m), (lines[0].Quantity, lines[0].UnitPrice));
        StringAssert.Contains(lines[0].Description, "1.300 GB medidos, 1.200 GB incluidos");
        Assert.AreEqual((2m, 1000m), (lines[1].Quantity, lines[1].UnitPrice));
        StringAssert.Contains(lines[1].Description, "12 GB medidos, 10 GB incluidos");
    }

    [TestMethod]
    public async Task ServicioSinCargoFijo_AlRenovarFacturaSoloElExcedente()
    {
        await using var w = await UsageWorld.Start(new FakeMeter().Definition());
        var h = w.H;
        await w.Metric("disk_gb", included: 0m);
        await w.Price("disk_gb", 100m);
        var id = await w.ActiveService();

        // Con descuento total el período no tiene nada que cobrar, pero el uso sí.
        await h.Db(async db =>
        {
            (await db.Services.SingleAsync(s => s.Id == id)).DiscountPercent = 100m;
            await db.SaveChangesAsync();
        });
        await w.Samples(id, "disk_gb", Nov2, 3m, 4m, 2m);

        h.Clock.Now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        await h.Use<BillingRunService, int>(s => s.Run());

        var invoice = (await w.Invoices("renewal")).Single();
        Assert.AreEqual(LineKind.Usage, invoice.Lines.Single().Kind);
        Assert.AreEqual(400m, invoice.Total);
        Assert.AreEqual(new DateOnly(2027, 1, 2), await h.Query(db => db.Services.Where(s => s.Id == id).Select(s => s.NextDueDate).SingleAsync()));
    }

    [TestMethod]
    public async Task CierreDelMes_FacturaDeUsoConSuPeriodo_SinExcedenteNoFactura_YLaMoraNoLaAnulaNiReactivaAlPagarla()
    {
        var meter = new FakeMeter();
        await using var w = await UsageWorld.Start(meter.Definition(), o => o.CancelUnpaidAfterDays = 7);
        var h = w.H;
        await w.Metric("disk_gb", included: 10m, UsageCycle.CalendarMonth);
        await w.Metric("mailboxes", included: 5m, UsageCycle.CalendarMonth);
        await w.Price("disk_gb", 1000m);
        var id = await w.ActiveService();
        await w.Samples(id, "disk_gb", Nov2, [.. Enumerable.Range(0, 29).Select(i => i == 10 ? 13m : 8m)]);
        await w.Samples(id, "mailboxes", Nov2, [.. Enumerable.Repeat(3m, 29)]);

        // El cierre se encola el 1/12 y espera 6 horas a las lecturas del último día.
        await w.Day(new DateOnly(2026, 11, 30));
        Assert.IsEmpty(await w.Invoices(UsageBilling.InvoiceReason));
        h.Clock.Now = h.Clock.Now.Add(UsageJobs.CloseDelay);
        await h.RunJobs();

        var invoice = (await w.Invoices(UsageBilling.InvoiceReason)).Single();
        var line = invoice.Lines.Single();
        Assert.AreEqual("Uso de Disco de VPS 2 GB (vps-01) del 02/11/2026 al 30/11/2026: 13 GB medidos, 10 GB incluidos", line.Description);
        Assert.AreEqual((LineKind.Usage, 3m, 1000m), (line.Kind, line.Quantity, line.UnitPrice));
        Assert.AreEqual(3000m, invoice.Total);
        Assert.AreEqual(new DateOnly(2026, 12, 8), invoice.DueDate);
        var charges = await h.Query(db => db.Set<UsageCharge>().OrderBy(c => c.Code).ToListAsync());
        Assert.AreEqual(("disk_gb", 3m, (long?)line.Id), (charges[0].Code, charges[0].Excess, charges[0].DocumentLineId));
        Assert.AreEqual(("mailboxes", 0m, (long?)null), (charges[1].Code, charges[1].Excess, charges[1].DocumentLineId), "Sin excedente la ventana se cierra igual, sin renglón.");

        // El comprobante fiscal informa el período del uso.
        var period = await h.Query(async db =>
        {
            var doc = await db.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == invoice.Id);
            var issuer = new IssuerProfile { PointOfSale = 3, TypeMap = """{"B":{"Invoice":6,"DebitNote":7,"CreditNote":8}}""" };
            var payload = await FiscalPayload.Build(db, doc, (await h.Use<BillingCore, ReceiverSnapshot>(c => c.Receiver(h.AccountId, CancellationToken.None))), issuer, new FiscalSeries(0, 3, 6), 1, new DateOnly(2026, 12, 1), CancellationToken.None);
            return payload["document"]!["service_period"]!;
        });
        Assert.AreEqual("2026-11-02", period["from"]!.GetValue<string>());
        Assert.AreEqual("2026-11-30", period["to"]!.GetValue<string>());

        // Sin pagar nada: la mora suspende el servicio y anula la renovación, pero la factura de uso queda.
        h.Clock.Now = new DateTimeOffset(2026, 12, 20, 12, 0, 0, TimeSpan.Zero);
        await h.Use<DunningService, int>(s => s.Run());
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Suspended, await h.Query(db => db.Services.Where(s => s.Id == id).Select(s => s.Status).SingleAsync()));
        Assert.AreEqual(InvoiceStatus.Cancelled, (await w.Invoices("renewal")).Single().Status);
        Assert.AreEqual(InvoiceStatus.Issued, (await w.Invoices(UsageBilling.InvoiceReason)).Single().Status);

        // Pagar el uso no reactiva un servicio suspendido por mora.
        await h.Use<PaymentService, long>(s => s.Record(new PaymentInput(h.AccountId, "manual:transferencia", "T-uso", Currency.Ars, 3000m, PaymentStatus.Approved, [invoice.Id])));
        await h.RunJobs();
        Assert.AreEqual(InvoiceStatus.Paid, (await w.Invoices(UsageBilling.InvoiceReason)).Single().Status);
        Assert.AreEqual(ServiceStatus.Suspended, await h.Query(db => db.Services.Where(s => s.Id == id).Select(s => s.Status).SingleAsync()));
        Assert.DoesNotContain("unsuspend", meter.Operations);
    }

    [TestMethod]
    public async Task PrecioPorMoneda_SinPrecioLaVentanaQuedaAbierta_YSeCobraAlCargarlo_ConLoIncluidoDeTodosSusMeses()
    {
        await using var w = await UsageWorld.Start(new FakeMeter().Definition());
        var h = w.H;
        var usd = await h.Use<AccountService, long>(s => s.Create(new AccountInput("Cliente del exterior", "exterior@example.com", Currency.Usd)));
        await h.Db(async db =>
        {
            db.PlanPrices.Add(new PlanPrice(h.PlanId, Currency.Usd, 20m, 0m, new DateOnly(2026, 1, 1), PriceMode.TaxExcluded));
            await db.SaveChangesAsync();
        });
        await w.Metric("disk_gb", included: 10m, UsageCycle.CalendarMonth);
        await w.Metric("transfer_gb", included: 100m, UsageCycle.CalendarMonth);
        await w.Price("disk_gb", 1000m);
        await w.Price("transfer_gb", 500m);
        var id = await w.ActiveService(usd);
        await w.Samples(id, "disk_gb", Nov2, 12m);
        await w.Samples(id, "transfer_gb", Nov2, 120m);

        // En noviembre no hay precio en dólares: no se corta y el panel lo muestra sin precio.
        await w.Day(new DateOnly(2026, 11, 30));
        h.Clock.Now = h.Clock.Now.Add(UsageJobs.CloseDelay);
        await h.RunJobs();
        Assert.IsEmpty(await w.Invoices(UsageBilling.InvoiceReason));
        Assert.AreEqual(0, await h.Query(db => db.Set<UsageCharge>().CountAsync()));
        var view = (await h.Use<UsageQueries, UsageView?>(q => q.ForService(id)))!;
        Assert.AreEqual((2m, (decimal?)null), (view.Metrics.Single(m => m.Code == "disk_gb").Excess, view.Metrics.Single(m => m.Code == "disk_gb").UnitPrice));
        Assert.AreEqual((100m, 20m), (view.Metrics.Single(m => m.Code == "transfer_gb").Included, view.Metrics.Single(m => m.Code == "transfer_gb").Excess));

        // Con el precio cargado desde antes, el cierre de diciembre cobra la ventana entera en su moneda.
        await w.Price("disk_gb", 0.75m, Currency.Usd, new DateOnly(2026, 10, 1));
        await w.Price("transfer_gb", 0.01m, Currency.Usd, new DateOnly(2026, 10, 1));
        await w.Samples(id, "disk_gb", new DateOnly(2026, 12, 1), 13m);
        await w.Samples(id, "transfer_gb", new DateOnly(2026, 12, 1), 50m);
        await w.Day(new DateOnly(2026, 12, 31));
        h.Clock.Now = h.Clock.Now.Add(UsageJobs.CloseDelay);
        await h.RunJobs();

        var invoice = (await w.Invoices(UsageBilling.InvoiceReason)).Single();
        Assert.AreEqual(Currency.Usd, invoice.Currency);
        Assert.AreEqual((3m, 0.75m, 2.25m), (invoice.Lines.Single().Quantity, invoice.Lines.Single().UnitPrice, invoice.Lines.Single().Amount));

        // El precio del excedente se lee en el modo de carga del servicio, como su renovación: en dólares, sin IVA.
        Assert.AreEqual(PriceMode.TaxExcluded, invoice.PriceMode);
        Assert.AreEqual(2.25m, invoice.NetTaxed + invoice.Exempt + invoice.NotTaxed);
        StringAssert.Contains(invoice.Lines.Single().Description, "del 02/11/2026 al 31/12/2026");

        // La transferencia de los dos meses (170 GB) entra en lo incluido de los dos (200 GB): no hay excedente.
        var transfer = await h.Query(db => db.Set<UsageCharge>().SingleAsync(c => c.Code == "transfer_gb"));
        Assert.AreEqual((new DateOnly(2026, 11, 2), new DateOnly(2027, 1, 1), 170m, 200m, 0m, (long?)null),
            (transfer.Start, transfer.End, transfer.Measured, transfer.Included, transfer.Excess, transfer.DocumentLineId));
    }

    [TestMethod]
    public async Task ServicioDadoDeBaja_ElCierreDelMesCobraSuUltimoUsoConLaRenovacion()
    {
        await using var w = await UsageWorld.Start(new FakeMeter().Definition());
        var h = w.H;
        await w.Metric("transfer_gb", included: 10m);
        await w.Price("transfer_gb", 200m);
        var id = await w.ActiveService();
        await w.Samples(id, "transfer_gb", Nov2, 6m, 6m);
        await h.Use<JobQueue, bool>(async jobs =>
        {
            await jobs.Enqueue(JobKinds.ServiceOperation, new ServiceOperation(id, "terminate", 2), $"service:{id}", $"service:{id}:terminate:2");
            return true;
        });
        await h.RunJobs();
        Assert.AreEqual(ServiceStatus.Terminated, await h.Query(db => db.Services.Where(s => s.Id == id).Select(s => s.Status).SingleAsync()));

        await w.Day(new DateOnly(2026, 11, 30));
        h.Clock.Now = h.Clock.Now.Add(UsageJobs.CloseDelay);
        await h.RunJobs();

        var invoice = (await w.Invoices(UsageBilling.InvoiceReason)).Single();
        Assert.AreEqual((2m, 400m), (invoice.Lines.Single().Quantity, invoice.Total));
    }

    [TestMethod]
    public async Task Configuracion_ValidaContraElManifiesto_YQuedaEnLaAuditoria()
    {
        await using var w = await UsageWorld.Start(new FakeMeter().Definition());
        var h = w.H;

        var unknown = await Assert.ThrowsExactlyAsync<ValidationException>(() => w.Metric("cpu_hours", included: 1m));
        StringAssert.Contains(unknown.Message, "cpu_hours");
        await Assert.ThrowsExactlyAsync<ValidationException>(() => w.Metric("transfer_gb", included: 1m, aggregation: UsageAggregation.Max));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => w.Metric("disk_gb", included: 1m, step: 0m));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => w.Price("disk_gb", 10m));

        var metric = await w.Metric("disk_gb", included: 10m, UsageCycle.CalendarMonth, UsageAggregation.Average, 0.5m, [90]);
        Assert.AreEqual(("Disco", "GB", UsageAggregation.Average, 0.5m), (metric.Name, metric.Unit, metric.Aggregation, metric.Step));
        CollectionAssert.AreEqual(new[] { 90 }, metric.AlertPercents);
        await w.Price("disk_gb", 10m);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => w.Price("disk_gb", 12m));
        await Assert.ThrowsExactlyAsync<ValidationException>(() => w.Price("disk_gb", -1m, from: new DateOnly(2026, 2, 1)));
        Assert.AreEqual(2, await h.Query(db => db.Set<AuditEvent>().CountAsync(a => a.Action == "usage.metric.save" || a.Action == "usage.price.add")));

        // Un producto sin módulo que lea el uso no puede medir.
        await h.Db(async db =>
        {
            (await db.Products.SingleAsync(p => p.Id == w.ProductId)).ModuleInstanceId = null;
            await db.SaveChangesAsync();
        });
        var noModule = await Assert.ThrowsExactlyAsync<ValidationException>(() => w.Metric("disk_gb", included: 10m));
        StringAssert.Contains(noModule.Message, UsageProtocol.Capability);
    }
}
