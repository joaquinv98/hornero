using System.Text.Json;
using System.Text.Json.Nodes;
using BillingOss.App.Platform;
using BillingOss.App.UseCases;
using BillingOss.Domain.Catalog;
using BillingOss.Domain.Fiscal;
using BillingOss.Domain.Services;
using BillingOss.Domain.Shared;
using BillingOss.Modules.Protocol;
using BillingOss.Modules.Sdk;
using BillingOss.Web;
using Microsoft.EntityFrameworkCore;

namespace BillingOss.App.Tests;

/// <summary>
/// Conciliación después de una restauración o de una promoción con pérdida (D-14, punto 7 de la revisión de alta
/// disponibilidad): el fisco, la pasarela y los módulos siguieron adelante y la base volvió atrás.
/// </summary>
[TestClass]
public sealed class ReconcileTests
{
    private static Task<ReconcileReport> Reconcile(CoreHarness h, DateTimeOffset since, long gap = 0, bool dryRun = false) =>
        h.Use<ReconcileService, ReconcileReport>(s => s.Run(new ReconcileRequest(since, gap, dryRun), CancellationToken.None));

    /// <summary>La base vuelve a antes de emitir: sin comprobante, con la emisión pendiente y la serie en cero. El fisco no vuelve.</summary>
    private static Task Rewind(CoreHarness h) => h.Db(async db =>
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM fiscal.documents");
        await db.Database.ExecuteSqlRawAsync("UPDATE fiscal.emissions SET status = 'Pending', proposed_number = NULL, attempts = 0");
        await db.Database.ExecuteSqlRawAsync("UPDATE fiscal.series SET last_number = 0, last_date = NULL");
    });

    /// <summary>Un comprobante que el fisco tiene y ningún documento local explica.</summary>
    private static JsonObject Orphan(long number) => new()
    {
        ["point_of_sale"] = 3,
        ["type_code"] = 6,
        ["number"] = number,
        ["date"] = "2026-11-01",
        ["concept"] = "services",
        ["currency"] = "PES",
        ["receiver"] = new JsonObject { ["doc_type"] = 96, ["doc_number"] = "30111222" },
        ["amounts"] = new JsonObject { ["total"] = "777.00" },
    };

    [TestMethod]
    public async Task Restauracion_ElComprobanteDeUnDocumentoLocal_SeAdopta_YLaEmisionSigue()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        var placed = await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);
        await Rewind(h);

        var report = await Reconcile(h, h.Clock.Now.AddHours(-1));

        Assert.AreEqual(1, report.Adopted);
        Assert.IsEmpty(report.Findings);
        var fiscal = await h.Query(db => db.FiscalDocuments.SingleAsync());
        Assert.AreEqual(placed.InvoiceId, fiscal.DocumentId);
        Assert.AreEqual(1L, fiscal.Number);
        Assert.AreEqual("CAE1", fiscal.AuthorizationCode);
        Assert.AreEqual(EmissionStatus.Authorized, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        Assert.AreEqual(1L, (await h.Query(db => db.FiscalSeries.SingleAsync())).LastNumber);
        var audit = await h.Query(db => db.Set<AuditEvent>().AsNoTracking().SingleAsync(e => e.Action == ReconcileService.AuditAction));
        Assert.AreEqual(1, JsonNode.Parse(audit.Details!)!["adopted"]!.GetValue<int>());

        // La emisión sigue con el número siguiente, sin conflicto ni segundo CAE para el adoptado.
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(2, fisco.Authorized);
        Assert.AreEqual(2, await h.Query(db => db.FiscalDocuments.CountAsync()));
    }

    [TestMethod]
    public async Task Restauracion_ElComprobanteSinDocumento_EsHuerfano_YLaSerieAvanzaIgual()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        fisco.Register(Orphan(1));

        var report = await Reconcile(h, h.Clock.Now.AddHours(-1));

        var finding = report.Findings.Single();
        Assert.AreEqual(ReconcileFinding.FiscalOrphan, finding.Kind);
        Assert.AreEqual("3-6-1", finding.Subject);
        Assert.AreEqual("CAE1", JsonNode.Parse(finding.Details)!["authorization_code"]!.GetValue<string>());
        var stored = await h.Query(db => db.ReconcileFindings.SingleAsync());
        Assert.IsNull(stored.ResolvedAt);
        Assert.AreEqual(1L, (await h.Query(db => db.FiscalSeries.SingleAsync())).LastNumber);
        Assert.AreEqual(1d, (await h.Query(db => BusinessMetrics.Read(db, h.Clock.Now, CancellationToken.None))).Single(r => r.Name == "reconcile_findings" && r.Label == ReconcileFinding.FiscalOrphan).Value);

        // Correrla dos veces no duplica el hallazgo abierto, y la emisión sigue después del huérfano.
        await Reconcile(h, h.Clock.Now.AddHours(-1));
        Assert.AreEqual(1, await h.Query(db => db.ReconcileFindings.CountAsync()));
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(2L, (await h.Query(db => db.FiscalDocuments.SingleAsync())).Number);
    }

    [TestMethod]
    public async Task Restauracion_DosDocumentosIguales_NadieAdopta_YElStaffDecide()
    {
        // Dos facturas del mismo cliente, el mismo día y el mismo total: el comprobante puede ser de cualquiera.
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        var first = await FiscalFlowTests.Order(h);
        await h.RunJobs();
        var second = await FiscalFlowTests.Order(h);
        await Rewind(h);

        var report = await Reconcile(h, h.Clock.Now.AddHours(-1));

        var finding = report.Findings.Single();
        Assert.AreEqual(ReconcileFinding.FiscalAmbiguous, finding.Kind);
        CollectionAssert.AreEquivalent(
            new[] { first.InvoiceId!.Value, second.InvoiceId!.Value },
            JsonNode.Parse(finding.Details)!["candidates"]!.AsArray().Select(c => c!.GetValue<long>()).ToArray());
        Assert.AreEqual(0, await h.Query(db => db.FiscalDocuments.CountAsync()));
        Assert.AreEqual(1L, (await h.Query(db => db.FiscalSeries.SingleAsync())).LastNumber);

        // La emisión pendiente de la segunda factura sigue con el número siguiente.
        await h.RunJobs();
        Assert.AreEqual(2L, (await h.Query(db => db.FiscalDocuments.SingleAsync())).Number);
    }

    [TestMethod]
    public async Task Restauracion_LasEmisionesEnConflictoVuelvenAPendientes()
    {
        // Antes de conciliar, cada emisión frenó por la serie fuera de sincronía. Después de conciliar, el barrido las reemite.
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        fisco.Register(Orphan(1));
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        var emission = await h.Query(db => db.FiscalEmissions.SingleAsync());
        Assert.AreEqual(EmissionStatus.Pending, emission.Status);
        StringAssert.Contains(emission.LastError, "fuera de sincronía");
        await h.Db(db => db.Database.ExecuteSqlRawAsync("UPDATE fiscal.emissions SET status = 'Conflict'"));

        await Reconcile(h, h.Clock.Now.AddHours(-1));

        Assert.AreEqual(EmissionStatus.Pending, (await h.Query(db => db.FiscalEmissions.SingleAsync())).Status);
        // Con el reloj de la prueba: la cola corre lo vencido según ese reloj, y el now() de la base ya lo pasó.
        await h.Db(db => db.Database.ExecuteSqlAsync($"UPDATE platform.jobs SET status = 'Pending', attempts = 0, run_at = {h.Clock.Now} WHERE kind = 'fiscal.emit'"));
        await h.RunJobs();
        Assert.AreEqual(2L, (await h.Query(db => db.FiscalDocuments.SingleAsync())).Number);
    }

    [TestMethod]
    public async Task NumeracionComercial_SaltaConElMargen_YQuedaRegistrado()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _h = h;
        await using var _m = module;
        await FiscalFlowTests.Order(h);

        var report = await Reconcile(h, h.Clock.Now, gap: 100);

        var gap = report.Findings.Single(f => f.Kind == ReconcileFinding.CounterGap && f.Subject == "Invoice");
        var details = JsonNode.Parse(gap.Details)!;
        Assert.AreEqual(2L, details["from"]!.GetValue<long>());
        Assert.AreEqual(102L, details["to"]!.GetValue<long>());
        var next = await FiscalFlowTests.Order(h);
        Assert.AreEqual(102L, (await h.Query(db => db.Documents.SingleAsync(d => d.Id == next.InvoiceId))).Number);
    }

    [TestMethod]
    public async Task ModoDePrueba_InformaYNoEscribeNada()
    {
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _ = h;
        await using var __ = module;
        var gateway = new FakeGateway();
        await using var pasarela = await TestModule.Start(gateway.Definition(), h, TestModule.NewBox());
        gateway.Add("pay-7", "de-otro-sistema", "500.00");
        fisco.Register(Orphan(1));

        var report = await Reconcile(h, h.Clock.Now.AddDays(-1), gap: 50, dryRun: true);

        Assert.IsTrue(report.DryRun);
        Assert.AreEqual(1, report.UnmatchedPayments);
        Assert.IsTrue(report.Findings.Any(f => f.Kind == ReconcileFinding.FiscalOrphan));
        Assert.IsTrue(report.Findings.Any(f => f.Kind == ReconcileFinding.CounterGap));
        Assert.AreEqual(0, await h.Query(db => db.ReconcileFindings.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.UnmatchedPayments.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.Timeline.CountAsync()));
        Assert.AreEqual(0, await h.Query(db => db.FiscalSeries.CountAsync()), "la serie que el fisco tiene y la base no conoce no se crea en modo de prueba");
        Assert.AreEqual(1L, await h.Query(db => db.Database.SqlQuery<long>($"SELECT next AS \"Value\" FROM billing.counters WHERE kind = 'Invoice'").SingleAsync()));
        var audit = await h.Query(db => db.Set<AuditEvent>().AsNoTracking().SingleAsync(e => e.Action == ReconcileService.AuditAction));
        Assert.IsTrue(JsonNode.Parse(audit.Details!)!["dry_run"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Aprovisionamiento_UnRecursoConMarcaSinServicio_EsHallazgo_YUnModuloQueNoListaTambien()
    {
        var box = TestModule.NewBox();
        await using var h = await CoreHarness.Start(box);
        long serviceId = 0;
        var lists = new ModuleDefinition(new JsonObject { ["id"] = "proxmox", ["kinds"] = new JsonArray("provisioning"), ["capabilities"] = new JsonArray("provisioning.core", "provisioning.list") })
            .Operation("provisioning", "list", _ => Task.FromResult(ModuleResponse.Completed(new JsonObject
            {
                ["resources"] = new JsonArray(
                    new JsonObject { ["service_id"] = serviceId, ["external_ids"] = new JsonObject { ["vmid"] = "100" } },
                    new JsonObject { ["service_id"] = 999, ["external_ids"] = new JsonObject { ["vmid"] = "101" }, ["label"] = "vm-101" }),
                ["next_offset"] = null,
            })), mutating: false);
        var blind = new ModuleDefinition(new JsonObject { ["id"] = "hestia", ["kinds"] = new JsonArray("provisioning"), ["capabilities"] = new JsonArray("provisioning.core") });
        await using var listing = await TestModule.Start(lists, h, box);
        await using var unlisted = await TestModule.Start(blind, h, box);
        await h.Db(async db =>
        {
            var product = await db.Products.SingleAsync(p => p.Name == "VPS 2 GB");
            var service = new Service(h.AccountId, product.Id, BillingCycle.Monthly, Currency.Ars, 12100m, 1, h.Clock.Now) { ModuleInstanceId = listing.InstanceId };
            db.Services.Add(service);
            await db.SaveChangesAsync();
            serviceId = service.Id;
        });

        var report = await Reconcile(h, h.Clock.Now);

        var orphan = report.Findings.Single(f => f.Kind == ReconcileFinding.ProvisioningOrphan);
        Assert.AreEqual($"instance:{listing.InstanceId}:999", orphan.Subject);
        Assert.AreEqual("101", JsonNode.Parse(orphan.Details)!["external_ids"]!["vmid"]!.GetValue<string>());
        Assert.AreEqual($"instance:{unlisted.InstanceId}", report.Findings.Single(f => f.Kind == ReconcileFinding.ProvisioningUnlisted).Subject);
        Assert.HasCount(2, report.Findings);
    }

    [TestMethod]
    public async Task LineaDeTiempoNueva_ElPlanificadorConciliaDesdeLaUltimaMarca_UnaSolaVez()
    {
        var (h, module, _) = await FiscalFlowTests.Start();
        await using var _h = h;
        await using var _m = module;
        await h.Tick();
        var current = (await h.Query(db => db.Timeline.SingleAsync())).TimelineId;
        var seen = h.Clock.Now.AddHours(-3);
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.timeline SET timeline_id = {current + 1}, seen_at = {seen}"));

        await h.Tick();
        await h.Tick();

        var job = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == JobKinds.Reconcile));
        Assert.AreEqual(Subscriptions.FiscalEntity, job.EntityKey);
        Assert.AreEqual(seen, JsonDocument.Parse(job.Payload).RootElement.GetProperty("since").GetDateTimeOffset());
        await h.RunJobs();
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id))).Status);
        var audit = await h.Query(db => db.Set<AuditEvent>().AsNoTracking().SingleAsync(e => e.Action == ReconcileService.AuditAction));
        Assert.AreEqual(seen, JsonNode.Parse(audit.Details!)!["since"]!.GetValue<DateTimeOffset>());
        Assert.AreEqual(current, (await h.Query(db => db.Timeline.SingleAsync())).TimelineId);
    }

    [TestMethod]
    public async Task ConciliacionViva_LosTrabajosFiscalesEsperan_YElQueYaCorriaTerminaPrimero()
    {
        // Cada serie tiene su clave de entidad y la conciliación la clave fiscal suelta: la cola no los ordena.
        var (h, module, fisco) = await FiscalFlowTests.Start();
        await using var _h = h;
        await using var _m = module;
        await h.Use<JobQueue, bool>(async q =>
        {
            await q.Enqueue(JobKinds.Reconcile, new { since = h.Clock.Now.AddHours(-1), timeline = 0 }, Subscriptions.FiscalEntity, "platform.reconcile:prueba", runAt: h.Clock.Now.AddHours(1));
            return true;
        });

        // Con la conciliación pendiente la factura nueva no se emite, y la espera no gasta intentos.
        await FiscalFlowTests.Order(h);
        await h.RunJobs();
        Assert.AreEqual(0, fisco.Authorized);
        var emit = await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == JobKinds.FiscalEmit));
        Assert.AreEqual(JobStatus.Pending, emit.Status);
        Assert.AreEqual(0, emit.Attempts);

        // Una emisión que ya corría en otro nodo hace esperar a la conciliación.
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET status = 'Running', locked_by = 'otro-nodo', locked_until = now() + interval '1 hour' WHERE id = {emit.Id}"));
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET run_at = {h.Clock.Now} WHERE kind = {JobKinds.Reconcile}"));
        await h.RunJobs();
        Assert.AreEqual(JobStatus.Pending, (await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == JobKinds.Reconcile))).Status);

        // Termina la emisión de afuera (vuelve a la cola): corre la conciliación y después sale la factura.
        await h.Db(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE platform.jobs SET status = 'Pending', locked_by = NULL, locked_until = NULL, run_at = {h.Clock.Now} WHERE id = {emit.Id}"));
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.RunJobs();
        Assert.AreEqual(JobStatus.Done, (await h.Query(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == JobKinds.Reconcile))).Status);
        h.Clock.Now = h.Clock.Now.AddMinutes(1);
        await h.RunJobs();
        Assert.AreEqual(1, fisco.Authorized);
    }

    [TestMethod]
    public void ElComando_EntiendeSusOpciones_YRechazaLasQueNo()
    {
        Assert.IsTrue(ReconcileCommand.TryParse(["--since", "2026-10-27T18:00:00Z", "--counter-gap", "50", "--dry-run"], out var parsed, out _));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero), parsed.Since);
        Assert.AreEqual(50L, parsed.CounterGap);
        Assert.IsTrue(parsed.DryRun);

        Assert.IsTrue(ReconcileCommand.TryParse([], out var empty, out _));
        Assert.IsNull(empty.Since);
        Assert.AreEqual(0L, empty.CounterGap);

        Assert.IsFalse(ReconcileCommand.TryParse(["--counter-gap", "-5"], out _, out var error));
        StringAssert.Contains(error, ReconcileCommand.Usage);
        Assert.IsFalse(ReconcileCommand.TryParse(["--since", "ayer"], out _, out _));
    }
}
